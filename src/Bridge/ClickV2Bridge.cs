using System.Security.Cryptography;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using ZwiftClickV2.Bridge.BLE;
using ZwiftClickV2.Bridge.Crypto;
using ZwiftClickV2.Bridge.Protocol;
using ZwiftClickV2.Bridge.Protocol.Messages;

namespace ZwiftClickV2.Bridge.Bridge;

/// <summary>
/// Orquestador principal del ZwiftClickV2-Bridge.
/// Flujo completo:
/// 1. Escanear y conectar BLE
/// 2. Leer característica 00000006 (info del dispositivo)
/// 3. Handshake ECDH (Hello → Welcome)
/// 4. Detectar versión del protocolo (V1/V2)
/// 5. Inicializar cifrado
/// 6. Enviar write ZOP post-handshake (Capability)
/// 7. Escuchar eventos de botones en CH02
/// Nota: keep-alive ausente en implementaciones de referencia (jat255/app.py, x.c)
/// </summary>
public class ClickV2Bridge : IDisposable
{
    private readonly BleDeviceManager _ble = new();
    private readonly BleCharacteristicWriter _writer = new();
    private readonly BleNotificationListener _listener = new();
    private readonly KeyboardEmulator _keyboard = new();
    private readonly MysteryPacketAnalyzer _mysteryAnalyzer = new();

    private ZopSequencer? _sequencer;
    private IZPEncryption? _crypto;
    private ECDiffieHellman? _ourEcdhKey;

    public bool IsOperational => _ble.IsConnected && _crypto?.IsInitialized == true;

    /// <summary>
    /// Inicia el bridge completo.
    /// </summary>
    public async Task<bool> StartAsync(string deviceName = "Zwift Click")
    {
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine("  ZwiftClickV2-Bridge v2.0");
        Console.WriteLine("═══════════════════════════════════════════\n");

        // 1. Conectar BLE
        var device = await _ble.ConnectAsync(deviceName);
        if (device == null) return false;

        // 2. Obtener características
        var (syncTx, syncRx, eventCh, ch06) = await GetCharacteristicsAsync();
        if (syncTx == null || syncRx == null) { Console.WriteLine("❌ SyncTx/SyncRx no encontrados"); return false; }

        _writer.RegisterCharacteristic(BleDeviceManager.CH03_UUID, syncTx);
        _writer.RegisterCharacteristic(BleDeviceManager.CH02_UUID, eventCh!);

        // 3. Leer 00000006 (si existe)
        if (ch06 != null)
        {
            var data06 = await _ble.ReadCharacteristic06Async();
            Console.WriteLine($"📋 CH06 (device info): {(data06 != null ? BitConverter.ToString(data06).Replace("-", " ") : "N/A")}");
        }

        // 4. Handshake ECDH
        var handshakeResult = await PerformHandshakeAsync(syncTx, syncRx);
        if (!handshakeResult.success) return false;

        // 5. Post-handshake: enviar writes ZOP
        await SendPostHandshakeMessagesAsync();

        // 6. Suscribirse a eventos de botones
        await SubscribeToButtonEventsAsync(eventCh);

        Console.WriteLine("✅ Bridge operativo — escuchando eventos de botones...");
        return true;
    }

    private async Task<(GattCharacteristic? syncTx, GattCharacteristic? syncRx, GattCharacteristic? eventCh, GattCharacteristic? ch06)>
        GetCharacteristicsAsync()
    {
        var ch03 = await _ble.GetCharacteristicAsync(BleDeviceManager.ZWIFT_SERVICE_UUID, BleDeviceManager.CH03_UUID);
        var ch04 = await _ble.GetCharacteristicAsync(BleDeviceManager.ZWIFT_SERVICE_UUID, BleDeviceManager.CH04_UUID);
        var ch02 = await _ble.GetCharacteristicAsync(BleDeviceManager.ZWIFT_SERVICE_UUID, BleDeviceManager.CH02_UUID);
        var ch06 = await _ble.GetCharacteristicAsync(BleDeviceManager.ZWIFT_SERVICE_UUID, BleDeviceManager.CH06_UUID);

        Console.WriteLine($"   CH03 (SyncTx): {(ch03 != null ? "✅" : "❌")}");
        Console.WriteLine($"   CH04 (SyncRx): {(ch04 != null ? "✅" : "❌")}");
        Console.WriteLine($"   CH02 (Events): {(ch02 != null ? "✅" : "❌")}");
        Console.WriteLine($"   CH06 (Info):   {(ch06 != null ? "✅" : "❌")}");

        return (ch03, ch04, ch02, ch06);
    }

    private async Task<(bool success, byte[]? response)> PerformHandshakeAsync(
        GattCharacteristic syncTx, GattCharacteristic syncRx)
    {
        Console.WriteLine("\n🤝 Iniciando handshake ECDH...");

        // Generar par ECDH
        _ourEcdhKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ourParams = _ourEcdhKey.ExportParameters(false);
        byte[] ourPubKey65 = new byte[65];
        ourPubKey65[0] = 0x04;
        Array.Copy(ourParams.Q.X!, 0, ourPubKey65, 1, 32);
        Array.Copy(ourParams.Q.Y!, 0, ourPubKey65, 33, 32);

        // Enviar Hello con sufijo 0x01 0x02
        var hello = new ZopHello { PublicKey = ourPubKey65, Suffix = new byte[] { 0x01, 0x02 } };
        byte[] helloPayload = hello.BuildHandshakePayload();
        Console.WriteLine($"   📤 Hello ({helloPayload.Length}B): {BitConverter.ToString(helloPayload.Take(32).ToArray()).Replace("-", "")}...");

        var responseTcs = new TaskCompletionSource<byte[]>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Suscribir a SyncRx
        await _listener.SubscribeAsync(BleDeviceManager.CH04_UUID, syncRx, data =>
        {
            Console.WriteLine($"   📥 Response ({data.Length}B): {BitConverter.ToString(data.Take(64).ToArray()).Replace("-", "")}...");
            responseTcs.TrySetResult(data);
        }, useIndicate: true);

        // Escribir Hello
        await _writer.WriteRawAsync(BleDeviceManager.CH03_UUID, helloPayload);

        byte[]? response = null;
        try { response = await responseTcs.Task.WaitAsync(cts.Token); }
        catch (TimeoutException) { Console.WriteLine("   ❌ Timeout esperando Welcome"); return (false, null); }

        // Parsear respuesta
        var parsed = ZopWelcome.Parse(response);
        Console.WriteLine($"   🏷️  Sufijo: {BitConverter.ToString(parsed.Suffix).Replace("-", " ")}");

        if (parsed.PublicKey.Length != 65)
        {
            Console.WriteLine("   ❌ No se encontró clave pública en la respuesta");
            return (false, response);
        }

        Console.WriteLine($"   🔑 Peer pubkey: {BitConverter.ToString(parsed.PublicKey.Take(16).ToArray()).Replace("-", "")}...");

        // Detectar versión y crear cifrado
        var version = ZPEncryptionFactory.DetectVersion(parsed.Suffix);
        Console.WriteLine($"   📐 Protocolo detectado: {version}");

        _crypto = ZPEncryptionFactory.Create(version);
        if (version == ZPEncryptionFactory.ProtocolVersion.V2)
            _crypto.InitializeV2(_ourEcdhKey, parsed.PublicKey, parsed.PublicKey);
        else
            _crypto.InitializeV1(_ourEcdhKey, parsed.PublicKey);

        _sequencer = new ZopSequencer(_crypto);
        Console.WriteLine("   ✅ Handshake completado\n");
        return (true, response);
    }

    private async Task SendPostHandshakeMessagesAsync()
    {
        Console.WriteLine("📤 Enviando writes ZOP post-handshake...");

        // Write 1: Capability (payload vacío, seq=0)
        // Nota: solo este write está justificado por las implementaciones de referencia.
        // Keep-alive y pings posteriores están confirmados como AUSENTES en jat255/app.py y x.c.
        byte[] capFrame = _sequencer!.PackageAndEncrypt(ZopCapability.CreateDefault().Payload);
        await _writer.WriteRawAsync(BleDeviceManager.CH03_UUID, capFrame);
        Console.WriteLine($"   ✅ Write 1 (Capability) seq=0: {capFrame.Length}B");
    }

    private async Task SubscribeToButtonEventsAsync(GattCharacteristic? eventCh)
    {
        if (eventCh == null) return;

        await _listener.SubscribeAsync(BleDeviceManager.CH02_UUID, eventCh, data =>
        {
            Console.WriteLine($"\n🎮 [CH02] ({data.Length}B): {BitConverter.ToString(data).Replace("-", "")}");

            // Intentar descifrar
            try
            {
                var (seq, plaintext) = _sequencer!.UnpackAndDecrypt(data);
                var evt = ZopPeripheralEvent.Parse(plaintext);
                Console.WriteLine($"   ✅ Descifrado seq={seq}: {evt}");

                // Emular tecla
                byte vk = evt.ToVirtualKey();
                if (vk != 0)
                {
                    _keyboard.SendKeyPress(vk);
                    Console.WriteLine($"   ⌨️  Tecla emulada: 0x{vk:X2}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ Decrypt falló: {ex.Message}");

                // Intentar analizar como paquete misterioso
                _mysteryAnalyzer.Analyze(data);
            }
        });
    }

    public void Stop()
    {
        _ble.Disconnect();
    }

    public void Dispose() => Stop();
}