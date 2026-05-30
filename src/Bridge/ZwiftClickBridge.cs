using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using ZwiftClickV2.Bridge.Auth;
using ZwiftClickV2.Bridge.BLE;
using ZwiftClickV2.Bridge.Crypto;
using ZwiftClickV2.Bridge.Logging;
using ZwiftClickV2.Bridge.Protocol;
using ZwiftClickV2.Bridge.Protocol.Messages;

namespace ZwiftClickV2.Bridge.Bridge;

/// <summary>
/// Bridge canónico del Zwift Click V2. Implementa la cadena de unlock CONFIRMADA
/// (ver docs/protocol/phaseC-dlock-auth.md):
///
///   1. BLE   handshake  "RideOn 02 03" + localPubKey[64]   (write CH03 ↔ indicate CH04)
///   2. HTTP  POST d-lock-service/device/authenticate  con Bearer del token de la cuenta
///            del usuario  →  204 No Content
///   3. BLE   write "FF 04 00" en CH03                       (señal de unlock, solo tras el 204)
///   4. BLE   sesión cifrada AES-256-CCM fluye en CH02 → eventos de botón → teclado
///
/// ⚠️ BLOQUEO ABIERTO: los campos 2 (id) y 3 (firma 40B) del request d-lock los emite el
/// dispositivo y la app los lee por BLE; su origen exacto aún no está resuelto
/// (ver <see cref="DeviceAuthChallenge"/>). Mientras no se resuelva, el bridge llega hasta el
/// handshake e informa con precisión dónde queda bloqueado, sin fabricar un request inválido.
/// </summary>
public sealed class ZwiftClickBridge : IDisposable
{
    private readonly BleDeviceManager _ble = new();
    private readonly BleCharacteristicWriter _writer = new();
    private readonly BleNotificationListener _listener = new();
    private readonly KeyboardEmulator _keyboard = new();
    private readonly StructuredLogger _logger = new();
    private readonly HkdfInfoMode _hkdfInfoMode;
    private readonly bool _emulateKeyboard;

    private ZPEncryptionV2? _crypto;
    private ZopSequencer? _sequencer;
    private ApplicationLayerParser? _appParser;
    private ECDiffieHellman? _ourKey;
    private byte[]? _ourPubKey65;
    private bool _stopped;

    public ZwiftClickBridge(HkdfInfoMode hkdfInfoMode = HkdfInfoMode.Empty, bool emulateKeyboard = true)
    {
        _hkdfInfoMode = hkdfInfoMode;
        _emulateKeyboard = emulateKeyboard;
    }

    public bool IsOperational => _ble.IsConnected && _crypto?.IsInitialized == true;

    /// <summary>
    /// Ejecuta la cadena de unlock. <paramref name="credentials"/> es la cuenta Zwift DEL USUARIO;
    /// si es null se omite la fase de red (solo diagnóstico de handshake).
    /// </summary>
    public async Task<bool> StartAsync(string deviceName, ZwiftCredentials? credentials)
    {
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine("  ZwiftClickV2-Bridge — unlock server-backed");
        Console.WriteLine("═══════════════════════════════════════════\n");

        var sw = Stopwatch.StartNew();

        // ── 1. Conectar BLE ─────────────────────────────────────────────
        var device = await _ble.ConnectAsync(deviceName);
        if (device == null) return false;

        var service = await _ble.GetServiceAsync(BleDeviceManager.ZWIFT_SERVICE_UUID);
        if (service == null)
        {
            Console.WriteLine("❌ Servicio ZAP (00000001-19CA-…) no encontrado.");
            return false;
        }

        var chars = (await service.GetCharacteristicsAsync()).Characteristics;
        var ch02 = chars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH02_UUID);
        var ch03 = chars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH03_UUID);
        var ch04 = chars.FirstOrDefault(c => c.Uuid == BleDeviceManager.CH04_UUID);

        if (ch03 == null || ch04 == null)
        {
            Console.WriteLine("❌ CH03/CH04 no encontrados (enlazar por UUID, los handles no son estables).");
            return false;
        }
        _writer.RegisterCharacteristic(BleDeviceManager.CH03_UUID, ch03);
        if (ch02 != null) _writer.RegisterCharacteristic(BleDeviceManager.CH02_UUID, ch02);

        // ── 2. Handshake "RideOn 02 03" ─────────────────────────────────
        Console.WriteLine("\n[1/4] Handshake BLE (RideOn 02 03)…");
        byte[]? deviceReply = await PerformHandshakeAsync(ch03, ch04);
        if (deviceReply == null)
        {
            Console.WriteLine("❌ Sin respuesta de handshake en CH04.");
            return false;
        }

        byte[]? devicePubKey64 = HandshakeParser.ExtractRawPublicKey(deviceReply);
        Console.WriteLine(devicePubKey64 != null
            ? "   ✅ Clave pública del dispositivo extraída (64B)."
            : "   ℹ️  El primer fragmento no traía pubkey (el firmware la envía en continuación). " +
              "Esto es esperado bajo DRM.");

        // ── 3. Fase de red (server-backed) ──────────────────────────────
        if (credentials == null)
        {
            Console.WriteLine("\n[2/4] Fase de red OMITIDA (sin credenciales). Solo diagnóstico de handshake.");
            return false;
        }

        if (!TryAssembleChallenge(devicePubKey64, deviceReply, out DeviceAuthChallenge? challenge))
        {
            Console.WriteLine("\n[2/4] ⛔ No se puede armar el request d-lock todavía.");
            Console.WriteLine("   Falta el origen de los campos 2 (id) y 3 (firma 40B) que emite el");
            Console.WriteLine("   dispositivo por BLE — bloqueo de investigación abierto. Ver");
            Console.WriteLine("   docs/protocol/phaseC-dlock-auth.md (sección 'Remaining').");
            _logger.LogInfo("Unlock blocked: device id/signature origin unresolved.");
            return false;
        }

        Console.WriteLine("\n[2/4] Login OAuth con tu cuenta Zwift + POST d-lock…");
        UnlockDecision decision;
        using (var oauth = new ZwiftOAuthClient())
        using (var unlock = new DeviceUnlockClient())
        {
            var coordinator = new UnlockCoordinator(oauth, unlock);
            decision = await coordinator.RunAsync(credentials, challenge!);
        }
        Console.WriteLine($"   d-lock → HTTP {decision.HttpStatusCode}: {decision.Notes}");
        _logger.LogInfo($"d-lock result: {decision.HttpStatusCode} authorized={decision.ShouldSendUnlockConfirm}");

        if (!decision.ShouldSendUnlockConfirm)
            return false;

        // ── 4. Unlock BLE (FF 04 00) + sesión cifrada ───────────────────
        Console.WriteLine("\n[3/4] Unlock BLE: write FF 04 00 → CH03…");
        await _writer.WriteRawAsync(BleDeviceManager.CH03_UUID, ZapCommands.UnlockConfirm);

        if (devicePubKey64 != null)
            InitializeSession(devicePubKey64);

        Console.WriteLine("\n[4/4] Suscribiendo CH02 (telemetría/botones cifrados)…");
        await SubscribeToEventsAsync(ch02);

        Console.WriteLine($"\n✅ Bridge operativo ({sw.ElapsedMilliseconds}ms).");
        return true;
    }

    private async Task<byte[]?> PerformHandshakeAsync(GattCharacteristic ch03, GattCharacteristic ch04)
    {
        _ourKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = _ourKey.ExportParameters(false);
        _ourPubKey65 = new byte[65];
        _ourPubKey65[0] = 0x04;
        Array.Copy(p.Q.X!, 0, _ourPubKey65, 1, 32);
        Array.Copy(p.Q.Y!, 0, _ourPubKey65, 33, 32);

        byte[] payload = ZapCommands.BuildHandshake(ZapCommands.V2Prefix, _ourPubKey65);

        var replyTcs = new TaskCompletionSource<byte[]>();
        await _listener.SubscribeAsync(BleDeviceManager.CH04_UUID, ch04, data =>
        {
            replyTcs.TrySetResult(data);
        }, useIndicate: true);

        await _writer.WriteRawAsync(BleDeviceManager.CH03_UUID, payload);
        _logger.Log("handshake_tx", "tx", "CH03", payload, "RideOn 02 03 + pubkey64");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            byte[] reply = await replyTcs.Task.WaitAsync(cts.Token);
            _logger.Log("handshake_rx", "rx", "CH04", reply, "device reply");
            return reply;
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            await _listener.UnsubscribeAsync(BleDeviceManager.CH04_UUID);
        }
    }

    /// <summary>
    /// Intenta armar el <see cref="DeviceAuthChallenge"/> a partir de lo provisto por el dispositivo.
    /// Devuelve false mientras el origen de id/firma siga sin resolverse. Cuando la investigación lo
    /// resuelva, este es el único punto a completar.
    /// </summary>
    private static bool TryAssembleChallenge(byte[]? devicePubKey64, byte[] deviceReply, out DeviceAuthChallenge? challenge)
    {
        challenge = null;
        if (devicePubKey64 == null)
            return false;

        // TODO(research): extraer DeviceId (campo 2) y Signature 40B (campo 3) de los datos que el
        // dispositivo envía por BLE (handshake en continuación o CH100/101/102). Sin esto, no hay
        // request válido — ver DeviceAuthChallenge y docs/protocol/phaseC-dlock-auth.md.
        return false;
    }

    private void InitializeSession(byte[] devicePubKey64)
    {
        _crypto = new ZPEncryptionV2(_hkdfInfoMode);
        _crypto.Initialize(_ourKey!, devicePubKey64, devicePubKey64, _ourPubKey65!);
        var adapter = ZPEncryptionFactory.CreateV2Adapter(_crypto);
        _sequencer = new ZopSequencer(adapter);
        _appParser = new ApplicationLayerParser(adapter, _sequencer);
        Console.WriteLine("   ✅ Sesión AES-256-CCM derivada.");
    }

    private async Task SubscribeToEventsAsync(GattCharacteristic? ch02)
    {
        if (ch02 == null || _appParser == null)
            return;

        await _listener.SubscribeAsync(BleDeviceManager.CH02_UUID, ch02, ctx =>
        {
            HandleNotification(ctx.AttributeHandle, ctx.Data);
        });
    }

    private void HandleNotification(ushort handle, byte[] data)
    {
        var parsed = _appParser!.Parse(handle, data);
        _logger.Log("app_rx", "rx", parsed.Channel.ToString(), parsed.Plaintext,
            $"opcode=0x{parsed.ApplicationOpcode:X2} {parsed.SymbolicName} encrypted={parsed.WasEncrypted}");

        if (!parsed.WasEncrypted || parsed.ApplicationOpcode != ZapWireOpcode.ZwiftClickNotification)
            return;

        byte[] eventPayload = parsed.Plaintext.AsSpan(1).ToArray();
        var evt = ZopPeripheralEvent.Parse(eventPayload);
        byte vk = evt.ToVirtualKey();
        if (vk != 0 && _emulateKeyboard)
        {
            _keyboard.SendKeyPress(vk);
            Console.WriteLine($"🎮 {evt} → tecla 0x{vk:X2}");
        }
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _ble.Disconnect();
        _logger.Dispose();
    }

    public void Dispose() => Stop();
}
