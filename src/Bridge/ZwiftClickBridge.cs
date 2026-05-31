using System.Diagnostics;
using System.Security.Cryptography;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using ZwiftClickV2.Bridge.Auth;
using ZwiftClickV2.Bridge.BLE;
using ZwiftClickV2.Bridge.Crypto;
using ZwiftClickV2.Bridge.Logging;
using ZwiftClickV2.Bridge.Protocol;

namespace ZwiftClickV2.Bridge.Bridge;

/// <summary>
/// Bridge canónico del Zwift Click V2. Implementa la cadena de unlock CONFIRMADA y validada en
/// hardware (ver docs/protocol/unlock-flow.md):
///
///   1. BLE   handshake  "RideOn 02 03" + localPubKey[64]   (write CH03)
///   2. BLE   el DISPOSITIVO emite EN CLARO en CH02 una trama ~85B: FF 03 00 ‖ protobuf(82B)
///            { 1: pubkey_comprimida, 2: id, 3: firma } — el reto, generado por el propio device
///   3. HTTP  POST d-lock-service/device/authenticate con Bearer del token del usuario y los 82B
///            VERBATIM (sin Content-Type)  →  204 No Content
///   4. BLE   write "FF 04 00" en CH03                       (señal de unlock, solo tras el 204)
///   5. BLE   sesión cifrada AES-256-CCM fluye en CH02 → eventos de botón → teclado
///
/// El bridge NO construye ni firma los campos del reto: los reenvía tal cual. La cripto solo hace
/// falta para decodificar los botones/telemetría DESPUÉS del unlock, no para desbloquear.
/// El estado <c>58 02</c> que puede devolver el device en CH04 NO es fatal: el reto llega igual.
/// </summary>
public sealed class ZwiftClickBridge : IDisposable
{
    private readonly BleDeviceManager _ble = new();
    private readonly BleCharacteristicWriter _writer = new();
    private readonly BleNotificationListener _listener = new();
    private readonly KeyboardEmulator _keyboard = new();
    private readonly StructuredLogger _logger = new();
    private readonly bool _emulateKeyboard;

    // Mapeo de botones del mando a teclas, indexado por un identificador estable de acción
    // ("plus", "minus", "left", "right", "emote_peace", …). Por defecto: cambio de marcha de
    // MyWhoosh (+ = subir = tecla I, − = bajar = tecla K). Configurable desde la interfaz.
    // El modelo es genérico: cualquier preset puede definir N acciones, cada una con su tecla.
    private readonly Dictionary<string, byte> _actionKeys = new()
    {
        ["minus"] = KeyboardEmulator.VK_K,
        ["plus"] = KeyboardEmulator.VK_I,
    };
    private readonly Dictionary<string, string> _actionLabels = new()
    {
        ["minus"] = "− (bajar)",
        ["plus"] = "+ (subir)",
    };

    private ECDiffieHellman? _ourKey;
    private byte[]? _ourPubKey65;

    // Cripto de sesión post-unlock: los candidatos del bake-off y el ganador (auto-resuelto del wire).
    private IReadOnlyList<SessionKeyBakeoff.Candidate>? _candidates;
    private SessionKeyBakeoff.Candidate? _session;
    private readonly List<byte[]> _postUnlockBuffer = new();

    private readonly TaskCompletionSource<DeviceAuthChallenge> _challengeTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _unlocked;
    private bool _stopped;

    private const int ChallengeTimeoutMs = 20_000;

    public ZwiftClickBridge(bool emulateKeyboard = true)
    {
        _emulateKeyboard = emulateKeyboard;
    }

    public bool IsOperational => _ble.IsConnected && _unlocked;

    /// <summary>
    /// Define qué tecla emula cada botón del mando. <paramref name="minusKey"/> es el botón "−"
    /// (evento izquierda) y <paramref name="plusKey"/> el botón "+" (evento derecha). Códigos de
    /// tecla virtual de Windows (ver <see cref="KeyboardEmulator"/>).
    /// </summary>
    public void SetKeyMapping(byte minusKey, byte plusKey)
    {
        _actionKeys["minus"] = minusKey;
        _actionKeys["plus"] = plusKey;
    }

    /// <summary>
    /// Reemplaza por completo el conjunto de acciones asignables: <paramref name="keys"/> mapea
    /// cada identificador de acción a su tecla virtual, y <paramref name="labels"/> a su etiqueta
    /// humana. Permite presets con cualquier número de botones (marchas, dirección, emotes, UI…).
    /// </summary>
    public void SetActions(IReadOnlyDictionary<string, byte> keys, IReadOnlyDictionary<string, string> labels)
    {
        _actionKeys.Clear();
        foreach (var kv in keys) _actionKeys[kv.Key] = kv.Value;
        _actionLabels.Clear();
        foreach (var kv in labels) _actionLabels[kv.Key] = kv.Value;
    }

    /// <summary>
    /// Canal de progreso OPCIONAL en lenguaje humano (lo usa la interfaz gráfica para explicar cada
    /// paso). Se dispara desde hilos de fondo: quien lo escuche debe marshalizar a su hilo de UI.
    /// La CLI no lo usa y sigue funcionando solo con <c>Console.WriteLine</c>.
    /// </summary>
    public event Action<BridgeProgress>? ProgressChanged;

    /// <summary>Se dispara cuando un botón del mando se tradujo a una tecla.</summary>
    public event Action<BridgeButtonEvent>? ButtonEmitted;

    /// <summary>
    /// Diagnóstico: cada trama CRUDA recibida en CH02 después del unlock (hex + interpretación).
    /// Sirve para mapear el formato real de los botones del Click V2 (hoy sin confirmar en hardware).
    /// </summary>
    public event Action<string>? DiagnosticFrame;

    /// <summary>Resultado de una calibración: (acción, firma asignada, éxito).</summary>
    public event Action<string, string, bool>? CalibrationFinished;

    /// <summary>Empieza un paso de calibración (manual o guiada): el identificador de la acción a pulsar.</summary>
    public event Action<string>? CalibrationStepStarted;

    /// <summary>Termina la calibración guiada (recorrió todas las acciones de la cola).</summary>
    public event Action? GuidedCalibrationFinished;

    private int _postUnlockFrameCount;

    // Estado del aprendiz de botones.
    private readonly Dictionary<string, int> _sigBaseline = new();    // firma → veces vista en reposo
    private readonly Dictionary<string, string> _sigToAction = new(); // firma calibrada → acción (override)
    private string? _runSig;     // firma de la racha en curso
    private int _runCount;       // longitud de la racha (distingue ráfaga de pulsación vs keepalive)
    private string? _calAction;

    // Umbral de ráfaga para emitir: ESTÁNDAR para TODOS los botones. Una pulsación real llega como
    // una ráfaga de ≥5 tramas IDÉNTICAS seguidas, sin interrupción de otro mensaje; el keepalive de
    // reposo llega aislado. Solo emitimos al cruzar este umbral, una sola vez por ráfaga. Cualquier
    // mensaje intermedio distinto reinicia la racha (ver _runSig/_runCount en OnControllerFrame).
    private const int EmitBurstThreshold = 5;

    // Perfil por defecto del Zwift Click V2 (firmas reales observadas en hardware, ver docs/BUTTON_MAP.md).
    // Permite que los botones funcionen SIN calibrar. La calibración del usuario tiene prioridad.
    // Coincidencia por PREFIJO; el orden importa (la firma más específica primero).
    private static readonly (string Prefix, string Action)[] ClickV2Profile =
    {
        ("08001064180020", "minus"), // botón − (bajar marcha)
        ("0810",           "plus"),  // botones + (subir marcha)
    };

    // Handshake CORTO "RideOn" + 02 03 (8B, sin pubkey) del protocolo V1/andriuz. Tras él + 000800/000810
    // el Click V2 (compatible con V1) emite los botones EN CLARO por CH02 como bitmask. Ref: andriuz29.
    private static readonly byte[] RideOnShort = { 0x52, 0x69, 0x64, 0x65, 0x4F, 0x6E, 0x02, 0x03 };
    private readonly Dictionary<string, int> _calCounts = new();
    private System.Threading.Timer? _calTimer;
    private readonly object _calLock = new();

    // Calibración guiada (automática): cola de acciones que se calibran una tras otra.
    private Queue<string>? _guidedQueue;
    private const int CalWindowMs = 5000;     // ventana de captura por acción
    private const int GuidedGapMs = 700;      // respiro entre pasos guiados
    private const int GuidedPrerollMs = 2500; // aprendizaje de baseline antes del primer paso
    // Una pulsación real emite una ráfaga (~10 tramas); el keepalive ~1/s ≈ 5 en la ventana. Si una
    // firma también aparece en reposo, exigimos superar este umbral para aceptarla como botón.
    private const int CalBurstThreshold = 8;

    private void Report(BridgePhase phase, string message, bool isError = false)
        => ProgressChanged?.Invoke(new BridgeProgress(phase, message, isError));

    /// <summary>
    /// Ejecuta la cadena de unlock. <paramref name="accessToken"/> es el Bearer de la cuenta Zwift
    /// del usuario (ya resuelto); si es null se omite la fase de red (solo captura el reto y reporta).
    /// </summary>
    public async Task<bool> StartAsync(string deviceName, string? accessToken)
    {
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine("  ZwiftClickV2-Bridge — unlock server-backed");
        Console.WriteLine("═══════════════════════════════════════════\n");

        var sw = Stopwatch.StartNew();

        // ── 1. Conectar BLE y resolver características por UUID ──────────
        Report(BridgePhase.Scanning, "Buscando tu mando por Bluetooth… enciéndelo o pulsa un botón.");
        var seenDevices = new List<string>();
        var device = await _ble.ConnectAsync(deviceName, onNewDeviceSeen: name =>
        {
            lock (seenDevices) seenDevices.Add(name);
            Report(BridgePhase.Scanning, $"📡 Veo cerca: «{name}»");
        });
        if (device == null)
        {
            string vistos;
            lock (seenDevices)
            {
                vistos = seenDevices.Count > 0
                    ? "Vi estos dispositivos Bluetooth: " + string.Join(", ", seenDevices) + ". "
                    : "No vi NINGÚN dispositivo anunciándose por Bluetooth. ";
            }
            Report(BridgePhase.Failed,
                $"No encontré un mando cuyo nombre contenga «{deviceName}». {vistos}" +
                "Prueba: pulsa un botón del mando para despertarlo; ciérralo en otras apps (móvil, Zwift) para que no esté ya conectado; " +
                "y si arriba ves el nombre real de tu mando, escríbelo en «Opciones avanzadas».", true);
            return false;
        }
        Report(BridgePhase.Connecting, "Mando encontrado. Abriendo el canal seguro…");

        // Resolver CH02/CH03/CH04 por UUID en TODOS los servicios (estén donde estén). En la práctica
        // basta con esto (no hace falta emparejar en Windows), así evitamos el diálogo nativo.
        var zap = await _ble.FindZapCharacteristicsAsync(onDiagnostic: msg => Report(BridgePhase.Connecting, msg));
        if (zap == null)
        {
            // Solo si no aparecen: algunos equipos exigen bonding. Lo intentamos UNA vez y reintentamos.
            Report(BridgePhase.Connecting, "No vi el canal de control; intento emparejar con Windows y reintento…");
            await _ble.EnsurePairedAsync(onDiagnostic: msg => Report(BridgePhase.Connecting, msg));
            zap = await _ble.FindZapCharacteristicsAsync(onDiagnostic: msg => Report(BridgePhase.Connecting, msg));
        }
        if (zap == null)
        {
            Console.WriteLine("❌ Características ZAP (CH02/CH03) no encontradas.");
            Report(BridgePhase.Failed,
                "El mando se conectó pero no pude localizar su canal de control (CH02/CH03). En el registro " +
                "verás qué servicios y características expone. Prueba a apagar y encender el Bluetooth de " +
                "Windows, o a emparejarlo a mano en Configuración → Bluetooth, y reintenta.", true);
            return false;
        }

        var ch02 = zap.Ch02;
        var ch03 = zap.Ch03;
        var ch04 = zap.Ch04;
        _writer.RegisterCharacteristic(BleDeviceManager.CH03_UUID, ch03);

        // Avisar si el mando se desconecta (clave: el enlace puede caerse tras el unlock).
        _ble.ConnectionChanged += connected =>
        {
            if (!connected)
                Report(BridgePhase.Failed, "⚠️ El mando se desconectó del Bluetooth. Acércalo al PC, mantenlo despierto y reconecta.", true);
        };

        // ── 2. Suscribir TODOS los canales del mando ────────────────────
        // El stream de botones puede llegar por CH02 (notify) o por otra característica. Suscribimos
        // CH02, CH04 y CH100/101/102 (si existen) y reportamos el estado de cada suscripción, para
        // no quedarnos a ciegas. Cada trama que llegue se vuelca al registro con su canal de origen.
        var sub02 = await _listener.SubscribeAsync(BleDeviceManager.CH02_UUID, ch02, OnCh02Frame);
        Report(BridgePhase.Connecting, $"Suscripción CH02 (botones): {sub02} · props {BleNotificationListener.DescribeProps(ch02)}");

        if (ch04 != null)
        {
            var sub04 = await _listener.SubscribeAsync(BleDeviceManager.CH04_UUID, ch04,
                d => OnOtherChannelFrame("CH04", d), useIndicate: true);
            Report(BridgePhase.Connecting, $"Suscripción CH04 (estado): {sub04} · props {BleNotificationListener.DescribeProps(ch04)}");
        }

        // CH100/101/102: por si los botones del Click V2 viajaran por aquí (todos tienen Notify).
        foreach (var (uuid, label) in new[]
        {
            (BleDeviceManager.CH100_UUID, "CH100"),
            (BleDeviceManager.CH101_UUID, "CH101"),
            (BleDeviceManager.CH102_UUID, "CH102"),
        })
        {
            if (zap.All.TryGetValue(uuid, out var extra))
            {
                var st = await _listener.SubscribeAsync(uuid, extra, d => OnOtherChannelFrame(label, d));
                Report(BridgePhase.Connecting, $"Suscripción {label}: {st} · props {BleNotificationListener.DescribeProps(extra)}");
            }
        }

        // ── 3. Handshake "RideOn 02 03" + pubkey[64] ────────────────────
        Console.WriteLine("\n[1/4] Handshake BLE (RideOn 02 03 + pubkey64)…");
        Report(BridgePhase.Handshake, "Saludo seguro: tu PC y el mando intercambian llaves (ECDH).");
        SendHandshake();

        // ── 4. Esperar el reto que emite el dispositivo en CH02 ─────────
        Console.WriteLine("[2/4] Esperando el reto del dispositivo en CH02…");
        Console.WriteLine("      (si no llega, despierta el Click pulsando un botón)");
        Report(BridgePhase.WaitingChallenge, "Esperando a que el mando cree su reto. Si tarda, pulsa un botón del mando.");
        DeviceAuthChallenge challenge;
        try
        {
            using var cts = new CancellationTokenSource(ChallengeTimeoutMs);
            challenge = await _challengeTcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"❌ No llegó el reto en {ChallengeTimeoutMs / 1000}s.");
            Report(BridgePhase.Failed, $"El mando no respondió en {ChallengeTimeoutMs / 1000}s. Despiértalo pulsando un botón y reintenta.", true);
            return false;
        }
        Console.WriteLine($"   ✅ Reto capturado: id={challenge.DeviceId}, body={challenge.Body.Length}B, " +
                          $"pubkey={Convert.ToHexString(challenge.DevicePublicKeyCompressed)[..12]}…");
        _logger.Log("auth_challenge", "rx", "CH02", challenge.Body, $"id={challenge.DeviceId}");
        Report(BridgePhase.ChallengeCaptured, "El mando creó su reto y tu PC lo recibió. Tu PC no lo firma: solo lo reenvía.");

        if (accessToken == null)
        {
            Console.WriteLine("\n[3/4] Fase de red OMITIDA (sin credenciales). Reto capturado correctamente.");
            Report(BridgePhase.Stopped, "Diagnóstico OK: el mando responde. Falta iniciar sesión con tu cuenta para desbloquear.");
            return false;
        }

        // ── 5. POST verbatim a d-lock-service ───────────────────────────
        Console.WriteLine("\n[3/4] POST d-lock-service/device/authenticate (body verbatim + Bearer)…");
        Report(BridgePhase.ServerAuth, "Comprobando con tu cuenta que este mando es tuyo (verificación en el servidor).");
        UnlockDecision decision;
        using (var unlock = new DeviceUnlockClient())
        {
            decision = await new UnlockCoordinator(unlock).RunAsync(accessToken, challenge.Body);
        }
        Console.WriteLine($"   d-lock → HTTP {decision.HttpStatusCode}: {decision.Notes}");
        _logger.LogInfo($"d-lock result: {decision.HttpStatusCode} authorized={decision.ShouldSendUnlockConfirm}");
        if (!decision.ShouldSendUnlockConfirm)
        {
            Report(BridgePhase.Failed, $"Tu cuenta no autorizó el mando (HTTP {decision.HttpStatusCode}). Revisa que has iniciado sesión con la cuenta dueña del mando.", true);
            return false;
        }

        // ── 6. Unlock BLE (FF 04 00) + sesión cifrada ───────────────────
        Console.WriteLine("\n[4/4] Unlock BLE: write FF 04 00 → CH03…");
        Report(BridgePhase.Unlocking, "¡Cuenta autorizada! Enviando la señal de desbloqueo al mando.");
        await _writer.WriteRawAsync(BleDeviceManager.CH03_UUID, ZapCommands.UnlockConfirm);

        SetupSessionBakeoff(challenge.DevicePublicKeyCompressed);
        _unlocked = true;

        // Pre-sembrar el keepalive puro como baseline con cuenta alta: nunca se elegirá en calibración
        // aunque ésta comience antes de haber acumulado observaciones en reposo. "080010" es la trama
        // de reposo/heartbeat del Click V2 (confirmado en hardware).
        // NOTA: 08001064180020 NO se pre-siembra porque aparece tanto como keepalive como botón −;
        // el aprendiz de baseline lo clasifica correctamente con las primeras observaciones reales.
        _sigBaseline["080010"] = 999;

        Console.WriteLine($"\n✅ UNLOCK COMPLETO ({sw.ElapsedMilliseconds}ms). Escuchando botones en CH02…");
        Console.WriteLine("   (la cripto de sesión se auto-resolverá con las primeras tramas cifradas)");
        Report(BridgePhase.Listening, "¡Listo! Mando desbloqueado. Calibra tus botones (botones «Calibrar +/−») pulsándolos cuando se te pida.");
        return true;
    }

    private void SendHandshake()
    {
        _ourKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = _ourKey.ExportParameters(false);
        _ourPubKey65 = new byte[65];
        _ourPubKey65[0] = 0x04;
        Array.Copy(p.Q.X!, 0, _ourPubKey65, 1, 32);
        Array.Copy(p.Q.Y!, 0, _ourPubKey65, 33, 32);

        byte[] payload = ZapCommands.BuildHandshake(ZapCommands.V2Prefix, _ourPubKey65);
        _ = _writer.WriteRawAsync(BleDeviceManager.CH03_UUID, payload);
        _logger.Log("handshake_tx", "tx", "CH03", payload, "RideOn 02 03 + pubkey64");
    }

    /// <summary>
    /// MODO V1 / andriuz (POR DEFECTO): conexión EN CLARO, SIN ECDH, SIN unlock de servidor y SIN
    /// cuenta Zwift. El Click V2 es compatible con el protocolo del Click V1: tras un RideOn corto y
    /// las escrituras de habilitación 000800/000810 en CH03, el mando emite los botones EN CLARO por
    /// CH02 como bitmask `23 08 B0 B1 B2 B3 0F` (activo-bajo: bit en 0 = botón pulsado). Decodificación
    /// determinista, sin calibración. Refs: andriuz29 · docs/EXTERNAL_BUTTON_MAP_RESEARCH.md.
    /// No toca el flujo de unlock V2 (StartAsync), que sigue disponible como modo alterno.
    /// </summary>
    public async Task<bool> StartAndriuzAsync(string deviceName)
    {
        Report(BridgePhase.Scanning, "Buscando tu mando por Bluetooth… enciéndelo o pulsa un botón.");
        var seenDevices = new List<string>();
        var device = await _ble.ConnectAsync(deviceName, onNewDeviceSeen: name =>
        {
            lock (seenDevices) seenDevices.Add(name);
            Report(BridgePhase.Scanning, $"📡 Veo cerca: «{name}»");
        });
        if (device == null)
        {
            Report(BridgePhase.Failed,
                $"No encontré un mando cuyo nombre contenga «{deviceName}». Pulsa un botón para despertarlo y reintenta.", true);
            return false;
        }
        Report(BridgePhase.Connecting, "Mando encontrado. Abriendo el canal…");

        var zap = await _ble.FindZapCharacteristicsAsync(onDiagnostic: msg => Report(BridgePhase.Connecting, msg));
        if (zap == null)
        {
            Report(BridgePhase.Connecting, "No vi el canal de control; intento emparejar con Windows y reintento…");
            await _ble.EnsurePairedAsync(onDiagnostic: msg => Report(BridgePhase.Connecting, msg));
            zap = await _ble.FindZapCharacteristicsAsync(onDiagnostic: msg => Report(BridgePhase.Connecting, msg));
        }
        if (zap == null)
        {
            Report(BridgePhase.Failed,
                "El mando se conectó pero no pude localizar su canal de control (CH02/CH03). Apaga y enciende el Bluetooth y reintenta.", true);
            return false;
        }

        _writer.RegisterCharacteristic(BleDeviceManager.CH03_UUID, zap.Ch03);
        _ble.ConnectionChanged += connected =>
        {
            if (!connected)
                Report(BridgePhase.Failed, "⚠️ El mando se desconectó del Bluetooth. Acércalo al PC, mantenlo despierto y reconecta.", true);
        };

        // Suscribir CH02 (botones). Por diagnóstico, también las CH02 extra, CH04 y CH100/101/102.
        await _listener.SubscribeAsync(BleDeviceManager.CH02_UUID, zap.Ch02, OnAndriuzFrame);
        Report(BridgePhase.Connecting, $"Suscripción CH02 (botones): props {BleNotificationListener.DescribeProps(zap.Ch02)}");
        foreach (var ch02 in zap.Ch02All)
            if (ch02.AttributeHandle != zap.Ch02.AttributeHandle)
                await _listener.SubscribeAsync(BleDeviceManager.CH02_UUID, ch02, OnAndriuzFrame);
        if (zap.Ch04 != null)
            await _listener.SubscribeAsync(BleDeviceManager.CH04_UUID, zap.Ch04, OnAndriuzFrame, useIndicate: true);
        foreach (var uuid in new[] { BleDeviceManager.CH100_UUID, BleDeviceManager.CH101_UUID, BleDeviceManager.CH102_UUID })
            if (zap.All.TryGetValue(uuid, out var extra))
                await _listener.SubscribeAsync(uuid, extra, OnAndriuzFrame);

        // Handshake CORTO (sin pubkey) + habilitación EN CLARO. NO se desbloquea ni se cifra nada.
        Report(BridgePhase.Handshake, "Saludo V1 en claro y habilitando los botones…");
        await _writer.WriteRawAsync(BleDeviceManager.CH03_UUID, RideOnShort);
        _logger.Log("handshake_tx", "tx", "CH03", RideOnShort, "RideOn 02 03 (corto, V1)");
        await Task.Delay(250);
        foreach (byte[] cmd in new[] { new byte[] { 0x00, 0x08, 0x00 }, new byte[] { 0x00, 0x08, 0x10 } })
        {
            await _writer.WriteRawAsync(BleDeviceManager.CH03_UUID, cmd);
            _logger.Log("enable_tx", "tx", "CH03", cmd, "habilitar botones (V1)");
            await Task.Delay(80);
        }

        _unlocked = true; // operativo: ya escuchamos botones (en V1 no hay unlock real)
        Console.WriteLine("\n✅ MODO V1 listo. Escuchando botones en claro (bitmask 2308…0F) en CH02…");
        Report(BridgePhase.Listening,
            "¡Listo! Modo sin cuenta. Pulsa los botones del mando: las marchas deberían moverse en MyWhoosh.");
        return true;
    }

    /// <summary>
    /// Mapa bit→acción del bitmask del Click V1/V2 (trama 23 08 B0 B1 B2 B3 0F, activo-bajo). El índice
    /// es el byte B0..B3 del bitmask. LOS 10 BOTONES CONFIRMADOS EN HARDWARE (Zwift Click V2, 2026-05-30,
    /// por orden de pulsación en el log de sesión). Mando izquierdo: d-pad + shifters. Mando derecho: a/b/y/z.
    /// </summary>
    private static readonly (int ByteIdx, byte Mask, string Action)[] AndriuzButtons =
    {
        // Mando IZQUIERDO
        (0, 0x01, "left"),     // ←
        (0, 0x02, "nav_up"),   // ↑
        (0, 0x04, "right"),    // →
        (0, 0x08, "nav_down"), // ↓
        (1, 0x02, "plus"),     // +  (corregido: + y − estaban invertidos)
        (1, 0x20, "minus"),    // −
        // Mando DERECHO (a/b/y/z, por orden de pulsación confirmado en hardware)
        (0, 0x20, "btn_a"),    // a
        (0, 0x10, "btn_b"),    // b
        (0, 0x40, "btn_x"),    // y
        (1, 0x01, "btn_y"),    // z
    };
    private readonly byte[] _prevMask = { 0xFF, 0xFF, 0xFF, 0xFF }; // reposo = todos los bits en 1

    /// <summary>Trama recibida en modo V1: decodifica el bitmask de botones o la vuelca al diagnóstico.</summary>
    private void OnAndriuzFrame(byte[] data)
    {
        _postUnlockFrameCount++;
        _logger.Log("andriuz_rx", "rx", "CH02", data, "raw");
        if (data.Length == 0) return;

        // Bitmask de botones: 23 08 B0 B1 B2 B3 0F. Activo-bajo: cada bit en 0 = botón pulsado.
        if (data.Length >= 6 && data[0] == 0x23 && data[1] == 0x08)
        {
            DecodeButtonBitmask(new[] { data[2], data[3], data[4], data[5] });
            return;
        }
        DiagnosticFrame?.Invoke($"#{_postUnlockFrameCount} [CH02] {Convert.ToHexString(data)}");
    }

    /// <summary>Emite una tecla por cada bit que pasa de 1→0 (flanco = botón recién pulsado).</summary>
    private void DecodeButtonBitmask(byte[] mask)
    {
        foreach (var (byteIdx, m, action) in AndriuzButtons)
        {
            bool wasDown = (_prevMask[byteIdx] & m) == 0;
            bool nowDown = (mask[byteIdx] & m) == 0;
            if (nowDown && !wasDown && _actionKeys.ContainsKey(action))
                EmitAction(action, $"2308{Convert.ToHexString(mask)}0F");
        }
        // Bits pulsados aún sin mapear (los ~4 del mando derecho): ofrecerlos para descubrirlos.
        for (int bi = 0; bi < 4; bi++)
            for (int bit = 0; bit < 8; bit++)
            {
                byte m = (byte)(1 << bit);
                bool wasDown = (_prevMask[bi] & m) == 0;
                bool nowDown = (mask[bi] & m) == 0;
                bool known = AndriuzButtons.Any(b => b.ByteIdx == bi && b.Mask == m);
                if (nowDown && !wasDown && !known)
                    DiagnosticFrame?.Invoke($"🆕 botón sin mapear: B{bi} bit 0x{m:X2} — dime cuál es físicamente y lo añado");
            }
        Array.Copy(mask, _prevMask, 4);
    }

    private void OnCh02Frame(byte[] data)
    {
        // Antes del unlock: buscar el reto en claro (FF 03 00 ‖ 0A 21 …).
        if (!_unlocked)
        {
            if (!_challengeTcs.Task.IsCompleted && DeviceAuthChallenge.TryParse(data, out var challenge))
                _challengeTcs.TrySetResult(challenge!);
            return;
        }

        // ── Tras el unlock ────────────────────────────────────────────────
        _postUnlockFrameCount++;
        _logger.Log("post_unlock_rx", "rx", "CH02", data, "raw");
        if (data.Length == 0) return;

        // Las notificaciones del Click V2 llegan EN CLARO (confirmado en hardware): el primer byte es
        // un opcode ZAP conocido (0x08 Play-notif, 0x38 Click, 0x28, 0x37, batería 0x23, etc.) o 0xFF
        // (re-reto). Si es así, lo tratamos como texto claro. Si no, intentamos el bake-off cifrado.
        if (IsPlaintextZap(data[0]))
        {
            DispatchPlaintext(data);
            return;
        }

        if (_session == null)
        {
            if (_candidates != null)
            {
                _postUnlockBuffer.Add(data);
                _session = SessionKeyBakeoff.Resolve(_candidates, _postUnlockBuffer, out _);
            }
            if (_session == null)
            {
                DiagnosticFrame?.Invoke($"#{_postUnlockFrameCount} [CH02] cifrada? ({data.Length}B) · {Convert.ToHexString(data)}");
                return;
            }
            Console.WriteLine($"   🔓 Cripto de sesión resuelta por bake-off: [{_session.Label}]");
            _logger.LogInfo($"Session crypto resolved: {_session.Label}");
            Report(BridgePhase.SessionResolved, $"Canal cifrado resuelto ({_session.Label}).");
            foreach (byte[] buffered in _postUnlockBuffer)
                if (_session.TryDecrypt(buffered, out var pt) && pt.Length > 0) DispatchPlaintext(pt);
            _postUnlockBuffer.Clear();
            return;
        }
        if (_session.TryDecrypt(data, out var ptx) && ptx.Length > 0)
            DispatchPlaintext(ptx);
        else
            DiagnosticFrame?.Invoke($"#{_postUnlockFrameCount} [CH02] no descifrable · {Convert.ToHexString(data)}");
    }

    /// <summary>
    /// Cualquier trama de un canal que NO sea CH02 (CH04, CH100/101/102). Tras el unlock se vuelca al
    /// registro: el stream de botones podría llegar por aquí en vez de por CH02.
    /// </summary>
    private void OnOtherChannelFrame(string channel, byte[] data)
    {
        _logger.Log("other_rx", "rx", channel, data, _unlocked ? "post-unlock" : "pre-unlock");
        if (!_unlocked || data.Length == 0) return;

        // Si parece una notificación de controlador, también la pasamos por el aprendiz de botones.
        if (IsControllerOpcode(data[0]))
        {
            OnControllerFrame(channel, data);
            return;
        }
        _postUnlockFrameCount++;
        DiagnosticFrame?.Invoke($"#{_postUnlockFrameCount} [{channel}] {DescribeOpcode(data[0])} · {Convert.ToHexString(data)}");
    }

    // ── Texto claro post-unlock ───────────────────────────────────────────
    private static bool IsPlaintextZap(byte op) => op is
        ZapWireOpcode.ZwiftPlayNotif or          // 0x08 — el Click V2 emite los botones aquí
        ZapWireOpcode.ZwiftClickNotification or  // 0x38
        ZapWireOpcode.ControllerNotification or  // 0x28
        ZapWireOpcode.ZwiftPlayDeviceStatus or   // 0x37
        ZapWireOpcode.BatteryStatus or           // 0x23
        ZapWireOpcode.Reset or                   // 0x19
        ZapWireOpcode.ControllerRequest or       // 0x15
        ZapWireOpcode.LostControl;               // 0xFF (re-reto)

    private static bool IsControllerOpcode(byte op) => op is
        ZapWireOpcode.ZwiftPlayNotif or
        ZapWireOpcode.ZwiftClickNotification or
        ZapWireOpcode.ControllerNotification or
        ZapWireOpcode.ZwiftPlayDeviceStatus;

    private static string DescribeOpcode(byte op) => op switch
    {
        ZapWireOpcode.ZwiftPlayNotif => "botón(0x08)",
        ZapWireOpcode.ZwiftClickNotification => "botón(0x38)",
        ZapWireOpcode.ControllerNotification => "ctrl-notif(0x28)",
        ZapWireOpcode.ZwiftPlayDeviceStatus => "estado(0x37)",
        ZapWireOpcode.BatteryStatus => "batería(0x23)",
        ZapWireOpcode.Reset => "reset(0x19)",
        ZapWireOpcode.ControllerRequest => "ctrl-req(0x15)",
        ZapWireOpcode.LostControl => "re-reto(0xFF)",
        _ => $"opcode 0x{op:X2}"
    };

    private void DispatchPlaintext(byte[] data)
    {
        byte op = data[0];
        if (op == ZapWireOpcode.LostControl)
        {
            // Re-reto del mando tras el unlock: ya estamos desbloqueados; lo ignoramos.
            DiagnosticFrame?.Invoke($"#{_postUnlockFrameCount} [CH02] re-reto del mando, ignorado ({data.Length}B)");
            return;
        }
        if (IsControllerOpcode(op))
        {
            OnControllerFrame("CH02", data);
            return;
        }
        // Batería/reset/etc.: estado en claro. Solo al log de archivo, sin ensuciar el registro visible.
        _logger.Log("status_rx", "rx", "CH02", data, DescribeOpcode(op));
    }

    // ── Aprendiz de botones (firma → acción) ──────────────────────────────
    // El formato exacto del 0x08 no está documentado por nadie. En vez de adivinar bytes, aprendemos
    // la "firma" (hex sin ceros finales) de cada botón por calibración: el reposo/keepalive se vuelve
    // baseline y se ignora; las firmas que el usuario asigna a +/− emiten su tecla.

    /// <summary>Normaliza una trama a su firma estable: hex sin los ceros finales (padding).</summary>
    public static string Signature(byte[] frame)
    {
        int end = frame.Length;
        while (end > 0 && frame[end - 1] == 0) end--;
        return Convert.ToHexString(frame, 0, end);
    }

    public bool IsCalibrating { get { lock (_calLock) return _calAction != null; } }

    /// <summary>true mientras una calibración guiada (automática) recorre la cola de acciones.</summary>
    public bool IsGuiding { get { lock (_calLock) return _guidedQueue != null; } }

    /// <summary>Etiqueta humana de una acción (su nombre en el preset, o el propio id si no hay).</summary>
    private string LabelOf(string action) => _actionLabels.TryGetValue(action, out var l) ? l : action;

    /// <summary>Inicia una ventana de calibración: el usuario pulsa el botón a asignar a <paramref name="action"/>.</summary>
    public void StartCalibration(string action)
    {
        lock (_calLock) { _calAction = action; _calCounts.Clear(); }
        CalibrationStepStarted?.Invoke(action);
        Report(BridgePhase.Listening, $"Calibrando: pulsa AHORA, varias veces, el botón para «{LabelOf(action)}» ({CalWindowMs / 1000} s)…");
        _calTimer?.Dispose();
        _calTimer = new System.Threading.Timer(_ => FinishCalibration(), null, CalWindowMs, System.Threading.Timeout.Infinite);
    }

    /// <summary>
    /// Calibración automática guiada: recorre <paramref name="actions"/> una a una, pidiendo cada
    /// botón y avanzando sola al siguiente. Antes del primer paso observa unos segundos de reposo
    /// para aprender el keepalive (baseline) y excluirlo. El usuario solo pulsa cada botón al pedírselo.
    /// </summary>
    public void StartGuidedCalibration(IEnumerable<string> actions)
    {
        var queue = new Queue<string>(actions);
        if (queue.Count == 0) { GuidedCalibrationFinished?.Invoke(); return; }
        lock (_calLock) { _guidedQueue = queue; }
        Report(BridgePhase.Listening, "Calibración automática: aprendiendo el reposo del mando… no pulses nada todavía.");
        // Pre-roll: deja que OnControllerFrame acumule baseline del keepalive antes de pedir el 1.er botón.
        _calTimer?.Dispose();
        _calTimer = new System.Threading.Timer(_ => AdvanceGuided(), null, GuidedPrerollMs, System.Threading.Timeout.Infinite);
    }

    /// <summary>Termina ya el paso de calibración en curso (sin esperar los 5 s). Útil para «saltar»
    /// un botón que el mando no tiene; en calibración guiada, avanza al siguiente.</summary>
    public void SkipCurrentCalibration()
    {
        bool active; lock (_calLock) active = _calAction != null;
        if (!active) return;
        _calTimer?.Dispose();
        FinishCalibration();
    }

    private void AdvanceGuided()
    {
        string? next;
        lock (_calLock) next = _guidedQueue is { Count: > 0 } q ? q.Peek() : null;
        if (next == null) { lock (_calLock) _guidedQueue = null; GuidedCalibrationFinished?.Invoke(); return; }
        StartCalibration(next);
    }

    private void FinishCalibration()
    {
        string action; string? best = null; int bestCount = 0;
        lock (_calLock)
        {
            if (_calAction == null) return;
            action = _calAction;
            foreach (var kv in _calCounts)
            {
                string sig = kv.Key; int count = kv.Value;

                // El keepalive PURO (reposo/heartbeat) nunca es un botón.
                if (sig == "080010") continue;

                // Excluir firmas ya asignadas a OTRA acción (evita solapamiento entre acciones).
                bool takenByOther = _sigToAction.TryGetValue(sig, out var existingAction) && existingAction != action;
                if (takenByOther) continue;

                // Detección por RÁFAGA en vez de por conteo acumulado: algunas firmas (p. ej.
                // 08001064180020) aparecen TANTO como keepalive (~1/s) COMO ráfaga de botón (~10 por
                // pulsación). Si la firma también se ve en reposo, exigimos una ráfaga clara para
                // aceptarla; si no, basta con verla unas pocas veces. Esto arregla el caso en que el
                // botón «−» no se detectaba porque su firma coincide con un keepalive ambiguo.
                bool alsoIdle = _sigBaseline.TryGetValue(sig, out var bc) && bc >= 3;
                int need = alsoIdle ? CalBurstThreshold : 3;
                if (count < need) continue;

                if (count > bestCount) { bestCount = count; best = sig; }
            }
            if (best != null)
            {
                foreach (var prev in _sigToAction.Where(p => p.Value == action).Select(p => p.Key).ToList())
                    _sigToAction.Remove(prev);
                _sigToAction[best] = action;
            }
            _calAction = null;
        }
        CalibrationFinished?.Invoke(action, best ?? "", best != null);

        // Calibración guiada: descolar la acción recién calibrada y programar la siguiente.
        bool guided;
        lock (_calLock)
        {
            guided = _guidedQueue is { Count: > 0 } q && q.Peek() == action;
            if (guided) _guidedQueue!.Dequeue();
        }
        if (guided)
        {
            _calTimer?.Dispose();
            _calTimer = new System.Threading.Timer(_ => AdvanceGuided(), null, GuidedGapMs, System.Threading.Timeout.Infinite);
        }
    }

    /// <summary>
    /// Carga un mapa de botones aprendido en sesiones anteriores (firma → acción). Tiene prioridad
    /// sobre el perfil por defecto del dispositivo, de modo que lo que el usuario calibró una vez
    /// sigue funcionando sin volver a calibrar.
    /// </summary>
    public void LoadLearnedMap(IReadOnlyDictionary<string, string> map)
    {
        lock (_calLock)
        {
            foreach (var kv in map)
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                    _sigToAction[kv.Key] = kv.Value;
        }
    }

    /// <summary>Olvida el mapeo aprendido (para recalibrar desde cero) y cancela la calibración guiada.</summary>
    public void ClearCalibration()
    {
        lock (_calLock) { _sigToAction.Clear(); _sigBaseline.Clear(); _calAction = null; _guidedQueue = null; }
        _calTimer?.Dispose();
        _sigBaseline["080010"] = 999; // mantener el keepalive conocido pre-sembrado
    }

    private void OnControllerFrame(string channel, byte[] data)
    {
        string sig = Signature(data);
        _logger.Log("ctrl_rx", "rx", channel, data, $"sig={sig}");

        // Calibrando: acumular y no emitir.
        lock (_calLock)
        {
            if (_calAction != null)
            {
                _calCounts[sig] = _calCounts.TryGetValue(sig, out var c) ? c + 1 : 1;
                return;
            }
        }

        // Aprender reposo (informativo/diagnóstico).
        int seen = _sigBaseline[sig] = _sigBaseline.TryGetValue(sig, out var bc2) ? bc2 + 1 : 1;

        // Racha de la firma actual: una PULSACIÓN llega como ráfaga (~10 tramas seguidas); el keepalive
        // de reposo llega aislado (intercalado con 080010). Contar la racha nos deja distinguir ambos
        // SIN calibrar, incluso cuando la firma del botón coincide con un keepalive ambiguo
        // (p. ej. 08001064180020). Así los botones funcionan «de fábrica».
        if (sig == _runSig) _runCount++;
        else { _runSig = sig; _runCount = 1; }

        // El keepalive puro nunca es un botón.
        if (sig == "080010") return;

        // Resolver acción: primero el mapeo calibrado (override del usuario), luego el perfil por
        // defecto del dispositivo (Click V2). Permite «listo para rodar» sin calibrar.
        string? action = ResolveAction(sig);
        if (action != null)
        {
            // Emitir UNA sola vez cuando la racha cruza el umbral (ráfaga real de pulsación),
            // nunca por un keepalive aislado en reposo.
            if (_runCount == EmitBurstThreshold)
                EmitAction(action, sig);
            return;
        }

        // Firma sin asignar y con pinta de pulsación (ráfaga): ofrecerla para calibrar.
        if (_runCount == EmitBurstThreshold && _postUnlockFrameCount > 8)
            DiagnosticFrame?.Invoke($"#{_postUnlockFrameCount} [{channel}] botón sin asignar · firma {sig}  (usa «Calibrar» para asignarlo)");
    }

    /// <summary>
    /// Resuelve la acción de una firma: primero el mapeo aprendido por calibración (exacto), luego el
    /// perfil por defecto del Click V2 (por prefijo). Solo devuelve acciones presentes en el preset actual.
    /// </summary>
    private string? ResolveAction(string sig)
    {
        lock (_calLock)
            if (_sigToAction.TryGetValue(sig, out var calibrated))
                return calibrated;

        foreach (var (prefix, act) in ClickV2Profile)
            if (sig.StartsWith(prefix, StringComparison.Ordinal) && _actionKeys.ContainsKey(act))
                return act;

        return null;
    }

    private void EmitAction(string action, string sig)
    {
        byte vk = _actionKeys.TryGetValue(action, out var k) ? k : (byte)0;
        string label = LabelOf(action);
        if (vk != 0 && _emulateKeyboard)
        {
            _keyboard.SendKeyPress(vk);
            Console.WriteLine($"🎮 {label} → tecla 0x{vk:X2}");
        }
        if (vk != 0)
        {
            ButtonEmitted?.Invoke(new BridgeButtonEvent(label, vk, action));
            Report(BridgePhase.ButtonPressed, $"Botón {label} → tecla enviada.");
        }
    }

    private void SetupSessionBakeoff(byte[] deviceCompressedPubKey33)
    {
        // La pubkey del dispositivo para el ECDH se recupera descomprimiendo el campo 1 del reto.
        // No fijamos un único modo cripto: construimos los 4 candidatos del bake-off y dejamos que
        // la validez del tag AES-CCM resuelva cuál es el correcto con las tramas reales de CH02.
        byte[] devicePubKey64 = EcPoint.Decompress(deviceCompressedPubKey33);
        byte[] ourPub64 = _ourPubKey65!.AsSpan(1, 64).ToArray();
        _candidates = SessionKeyBakeoff.BuildCandidates(_ourKey!, devicePubKey64, ourPub64);
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _calTimer?.Dispose();
        _ble.Disconnect();
        _logger.Dispose();
        Report(BridgePhase.Stopped, "Bridge detenido.");
    }

    public void Dispose() => Stop();
}
