using System.Diagnostics;
using System.Security.Cryptography;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using ZwiftClickV2.Bridge.Auth;
using ZwiftClickV2.Bridge.BLE;
using ZwiftClickV2.Bridge.Crypto;
using ZwiftClickV2.Bridge.Logging;
using ZwiftClickV2.Bridge.Protocol;
using ZwiftClickV2.Bridge.Protocol.Messages;

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
    /// Canal de progreso OPCIONAL en lenguaje humano (lo usa la interfaz gráfica para explicar cada
    /// paso). Se dispara desde hilos de fondo: quien lo escuche debe marshalizar a su hilo de UI.
    /// La CLI no lo usa y sigue funcionando solo con <c>Console.WriteLine</c>.
    /// </summary>
    public event Action<BridgeProgress>? ProgressChanged;

    /// <summary>Se dispara cuando un botón del mando se tradujo a una tecla.</summary>
    public event Action<BridgeButtonEvent>? ButtonEmitted;

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
        var device = await _ble.ConnectAsync(deviceName);
        if (device == null)
        {
            Report(BridgePhase.Failed, "No encontré el mando. Comprueba que el Bluetooth está activo y el mando despierto.", true);
            return false;
        }
        Report(BridgePhase.Connecting, "Mando encontrado. Abriendo el canal seguro…");

        var service = await _ble.GetServiceAsync(BleDeviceManager.ZWIFT_SERVICE_UUID);
        if (service == null)
        {
            Console.WriteLine("❌ Servicio ZAP (00000001-19CA-…) no encontrado.");
            Report(BridgePhase.Failed, "El mando respondió pero no expone su servicio esperado. Reinícialo e inténtalo de nuevo.", true);
            return false;
        }

        var chars = (await service.GetCharacteristicsAsync()).Characteristics;
        var ch02 = chars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH02_UUID);
        var ch03 = chars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH03_UUID);
        var ch04 = chars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH04_UUID);

        if (ch02 == null || ch03 == null)
        {
            Console.WriteLine("❌ CH02/CH03 no encontrados (enlazar por UUID; los handles no son estables).");
            Report(BridgePhase.Failed, "No encontré los canales del mando. Reinícialo e inténtalo de nuevo.", true);
            return false;
        }
        _writer.RegisterCharacteristic(BleDeviceManager.CH03_UUID, ch03);

        // ── 2. Suscribir CH02 (reto en claro + sesión cifrada) y CH04 (eco/estado) ──
        await _listener.SubscribeAsync(BleDeviceManager.CH02_UUID, ch02, OnCh02Frame);
        if (ch04 != null)
        {
            await _listener.SubscribeAsync(BleDeviceManager.CH04_UUID, ch04, OnCh04Frame, useIndicate: true);
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

        Console.WriteLine($"\n✅ UNLOCK COMPLETO ({sw.ElapsedMilliseconds}ms). Escuchando botones en CH02…");
        Console.WriteLine("   (la cripto de sesión se auto-resolverá con las primeras tramas cifradas)");
        Report(BridgePhase.Listening, "¡Listo! Tu mando está desbloqueado. Pulsa sus botones y se convertirán en teclas.");
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

    private void OnCh02Frame(byte[] data)
    {
        // Antes del unlock: buscar el reto en claro (FF 03 00 ‖ 0A 21 …).
        if (!_unlocked)
        {
            if (!_challengeTcs.Task.IsCompleted && DeviceAuthChallenge.TryParse(data, out var challenge))
                _challengeTcs.TrySetResult(challenge!);
            return;
        }

        // Tras el unlock: tráfico cifrado. Hasta resolver la cripto, acumular y probar el bake-off.
        if (_session == null)
        {
            if (_candidates == null) return;
            _postUnlockBuffer.Add(data);
            _session = SessionKeyBakeoff.Resolve(_candidates, _postUnlockBuffer, out _);
            if (_session == null) return; // seguir acumulando hasta que una trama valide su tag

            Console.WriteLine($"   🔓 Cripto de sesión resuelta por bake-off: [{_session.Label}]");
            _logger.LogInfo($"Session crypto resolved: {_session.Label}");
            Report(BridgePhase.SessionResolved, "Canal cifrado resuelto: los botones del mando ya se leen correctamente.");
            foreach (byte[] buffered in _postUnlockBuffer)
                EmitButton(buffered);
            _postUnlockBuffer.Clear();
            return;
        }

        EmitButton(data);
    }

    /// <summary>Descifra una trama CH02 con el candidato resuelto y, si es un evento de botón, emula la tecla.</summary>
    private void EmitButton(byte[] frame)
    {
        if (_session == null || !_session.TryDecrypt(frame, out byte[] plaintext) || plaintext.Length == 0)
            return;

        _logger.Log("app_rx", "rx", "CH02", plaintext, $"opcode=0x{plaintext[0]:X2}");
        if (plaintext[0] != ZapWireOpcode.ZwiftClickNotification)
            return;

        var evt = ZopPeripheralEvent.Parse(plaintext.AsSpan(1).ToArray());
        byte vk = evt.ToVirtualKey();
        if (vk != 0 && _emulateKeyboard)
        {
            _keyboard.SendKeyPress(vk);
            Console.WriteLine($"🎮 {evt} → tecla 0x{vk:X2}");
        }
        if (vk != 0)
        {
            string label = evt.Type switch
            {
                ZopPeripheralEvent.EventType.LeftClick or ZopPeripheralEvent.EventType.LeftHold => "Izquierda",
                ZopPeripheralEvent.EventType.RightClick or ZopPeripheralEvent.EventType.RightHold => "Derecha",
                _ => evt.Type.ToString()
            };
            ButtonEmitted?.Invoke(new BridgeButtonEvent(label, vk));
            Report(BridgePhase.ButtonPressed, $"Botón {label} → tecla enviada a la app activa.");
        }
    }

    private void OnCh04Frame(byte[] data)
    {
        // Eco/estado del handshake. Puede ser "RideOn 02 03 58 02 …" — el 58 02 NO es fatal:
        // el reto llega igual por CH02. Solo se registra para diagnóstico.
        _logger.Log("handshake_rx", "rx", "CH04", data, "device reply (58 02 status no es fatal)");
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
        _ble.Disconnect();
        _logger.Dispose();
        Report(BridgePhase.Stopped, "Bridge detenido.");
    }

    public void Dispose() => Stop();
}
