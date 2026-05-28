using System.Security.Cryptography;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using ZwiftClickV2.Bridge.Crypto;
using ZwiftClickV2.Bridge.Logging;

namespace ZwiftClickV2.Bridge.Protocol;

/// <summary>
/// Fuzzer exhaustivo de handshakes.
/// Prueba TODAS las combinaciones de prefijos + sufijos + tamaños de clave.
/// A diferencia del HandshakeProber, completa todas las variaciones
/// sin detenerse al encontrar una clave válida.
/// 
/// Variaciones probadas:
/// - Prefijos: "RideOn" + sufijo, solo pubkey
/// - Sufijos: 01 02, 02 03, 00 09, 01 01, sin sufijo
/// - Tamaños: 64B (sin 0x04), 65B (con 0x04)
/// - Total: ~15 variaciones
/// </summary>
public class HandshakeFuzzer
{
    private static readonly byte[] RIDE_ON = "RideOn"u8.ToArray();

    private static readonly Guid ZWIFT_SERVICE_UUID = Guid.Parse("0000fc82-0000-1000-8000-00805f9b34fb");
    private static readonly Guid CH02_UUID = Guid.Parse("00000002-19ca-4651-86e5-fa29dcdd09d1");
    private static readonly Guid CH03_UUID = Guid.Parse("00000003-19ca-4651-86e5-fa29dcdd09d1");
    private static readonly Guid CH04_UUID = Guid.Parse("00000004-19ca-4651-86e5-fa29dcdd09d1");
    private static readonly Guid CH06_UUID = Guid.Parse("00000006-19ca-4651-86e5-fa29dcdd09d1");
    private static readonly Guid CH102_UUID = Guid.Parse("00000102-19ca-4651-86e5-fa29dcdd09d1");

    private readonly StructuredLogger _logger;
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _ch03, _ch04, _ch02, _ch06, _ch102;
    private readonly List<FuzzResult> _results = new();

    public record FuzzResult(
        string VariationName,
        byte[] Payload,
        byte[]? Response,
        bool IsPublicKey,
        bool IsRejection,
        string Notes
    );

    public HandshakeFuzzer(StructuredLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Ejecuta el fuzzer completo: conectar, fuzzear todas las variaciones,
    /// capturar paquetes de 85B, intentar descifrar.
    /// </summary>
    public async Task<List<FuzzResult>> RunAsync()
    {
        _logger.LogInfo("=== HANDSHAKE FUZZER ===");

        // 1. Conectar
        if (!await ConnectAsync()) return _results;

        // 2. Leer CH06
        await ReadDeviceInfoAsync();

        // 3. Generar par ECDH
        using var ourKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ourParams = ourKey.ExportParameters(false);
        byte[] pubKey65 = BuildPubKey65(ourParams);
        byte[] pubKey64 = pubKey65.AsSpan(1, 64).ToArray();

        _logger.LogInfo($"🔑 Nuestra pubkey (65B): {BitConverter.ToString(pubKey65.Take(16).ToArray()).Replace("-", "")}...");

        // 4. Suscribirse a CH04 para recibir respuestas
        byte[]? lastResponse = null;
        var responseEvent = new ManualResetEventSlim(false);

        if (_ch04 != null)
        {
            _ch04.ValueChanged += (s, args) =>
            {
                var reader = DataReader.FromBuffer(args.CharacteristicValue);
                lastResponse = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(lastResponse);
                responseEvent.Set();
            };
            await _ch04.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Indicate);
        }

        // 5. Probar todas las variaciones
        var variations = BuildAllVariations(pubKey65, pubKey64);

        foreach (var (name, payload) in variations)
        {
            _logger.LogInfo($"\n🧪 Probando: {name}");
            _logger.Log("handshake_sent", "tx", "CH03", payload, name);

            responseEvent.Reset();
            lastResponse = null;

            // Enviar
            var writer = new DataWriter();
            writer.WriteBytes(payload);
            var status = await _ch03!.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
            _logger.LogInfo($"   Write status: {status}");

            // Esperar respuesta (3s)
            bool gotResponse = responseEvent.Wait(TimeSpan.FromSeconds(3));
            await Task.Delay(500); // Gap entre intentos

            if (!gotResponse || lastResponse == null)
            {
                _logger.LogInfo($"   ❌ TIMEOUT");
                _results.Add(new FuzzResult(name, payload, null, false, false, "TIMEOUT"));
                continue;
            }

            _logger.Log("response_received", "rx", "CH04", lastResponse, name);

            bool isRejection = HandshakeParser.Classify(lastResponse) == HandshakeParser.ResponseType.StatusMessage;
            bool isPubKey = HandshakeParser.Classify(lastResponse) == HandshakeParser.ResponseType.PublicKey;

            string notes = isPubKey ? "PUBLIC_KEY_DETECTED" :
                           isRejection ? "REJECTION (status data)" : "UNKNOWN";

            if (isPubKey)
            {
                var pk = HandshakeParser.ExtractPublicKey(lastResponse);
                if (pk != null)
                {
                    _logger.LogInfo($"   🎉 CLAVE PÚBLICA VÁLIDA: {BitConverter.ToString(pk.Take(16).ToArray()).Replace("-", "")}...");

                    // Intentar inicializar cifrado
                    try
                    {
                        var zp = new ZPEncryptionV2();
                        zp.Initialize(ourKey, pk, saltPublicKey65: pk);
                        _logger.LogInfo("   ✅ ZPEncryption inicializado correctamente");
                        notes += " | ENCRYPTION_OK";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"   ❌ ZPEncryption falló: {ex.Message}");
                        notes += " | ENCRYPTION_FAILED";
                    }
                }
            }
            else if (isRejection)
            {
                _logger.LogInfo($"   ⚠️  RECHAZO: {BitConverter.ToString(lastResponse.Take(16).ToArray()).Replace("-", "")}...");
            }
            else
            {
                _logger.LogInfo($"   ❓ RESPUESTA DESCONOCIDA: {BitConverter.ToString(lastResponse.Take(16).ToArray()).Replace("-", "")}...");
            }

            _results.Add(new FuzzResult(name, payload, lastResponse, isPubKey, isRejection, notes));
        }

        // 6. Resumen
        var pubKeyResults = _results.Where(r => r.IsPublicKey).ToList();
        _logger.LogInfo($"\n📊 RESUMEN: {_results.Count} variaciones, {pubKeyResults.Count} con clave pública");
        foreach (var r in pubKeyResults)
            _logger.LogInfo($"   🏆 {r.VariationName}: {r.Notes}");

        return _results;
    }

    /// <summary>
    /// Construye TODAS las variaciones de handshake (~15).
    /// </summary>
    private static List<(string Name, byte[] Payload)> BuildAllVariations(byte[] pubKey65, byte[] pubKey64)
    {
        var variations = new List<(string, byte[])>();

        // Prefijos "RideOn" + sufijo + clave 65B
        foreach (var suffix in new byte[][] {
            new byte[] { 0x01, 0x02 },
            new byte[] { 0x02, 0x03 },
            new byte[] { 0x00, 0x09 },
            new byte[] { 0x01, 0x01 }
        })
        {
            string suffixName = $"{suffix[0]:X2}{suffix[1]:X2}";
            variations.Add(($"RideOn+{suffixName}+65B", Concat(RIDE_ON, suffix, pubKey65)));
        }

        // Prefijos "RideOn" + sufijo + clave 64B (sin 0x04)
        foreach (var suffix in new byte[][] {
            new byte[] { 0x01, 0x02 },
            new byte[] { 0x02, 0x03 },
        })
        {
            string suffixName = $"{suffix[0]:X2}{suffix[1]:X2}";
            variations.Add(($"RideOn+{suffixName}+64B", Concat(RIDE_ON, suffix, pubKey64)));
        }

        // Solo clave (sin prefijo)
        variations.Add(("Solo_65B", pubKey65));
        variations.Add(("Solo_64B", pubKey64));

        // "RideOn" + 04 + clave64 (como si 0x04 fuera sufijo)
        variations.Add(("RideOn+04+64B", Concat(RIDE_ON, new byte[] { 0x04 }, pubKey64)));

        // "RideOn" sin sufijo + clave65 (RideOn directo + pubkey)
        variations.Add(("RideOn+65B", Concat(RIDE_ON, pubKey65)));
        variations.Add(("RideOn+64B", Concat(RIDE_ON, pubKey64)));

        // 00 09 + 64B
        variations.Add(("RideOn+0009+64B", Concat(RIDE_ON, new byte[] { 0x00, 0x09 }, pubKey64)));

        // 01 01 + 64B
        variations.Add(("RideOn+0101+64B", Concat(RIDE_ON, new byte[] { 0x01, 0x01 }, pubKey64)));

        return variations;
    }

    // ── BLE ─────────────────────────────────────────────────────────────

    private async Task<bool> ConnectAsync()
    {
        _logger.LogInfo("🔍 Escaneando 'Zwift Click' (30s)...");
        var tcs = new TaskCompletionSource<ulong>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

        watcher.Received += (s, args) =>
        {
            if (!string.IsNullOrEmpty(args.Advertisement.LocalName) &&
                args.Advertisement.LocalName.StartsWith("Zwift Click", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInfo($"   ✅ Encontrado: {args.Advertisement.LocalName} (0x{args.BluetoothAddress:X})");
                tcs.TrySetResult(args.BluetoothAddress);
            }
        };
        cts.Token.Register(() => tcs.TrySetCanceled());
        watcher.Start();

        ulong address;
        try { address = await tcs.Task; }
        catch (OperationCanceledException)
        {
            _logger.LogError("Timeout: no se encontró ningún Zwift Click");
            return false;
        }
        finally { watcher.Stop(); }

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        _logger.LogInfo($"📡 Conectado: {_device.Name}");

        // Obtener características
        var servicesResult = await _device.GetGattServicesAsync();
        var service = servicesResult.Services?.FirstOrDefault(s => s.Uuid == ZWIFT_SERVICE_UUID);
        if (service == null) { _logger.LogError("Servicio Zwift no encontrado"); return false; }

        var chars = await service.GetCharacteristicsAsync();
        _ch03 = chars.Characteristics.FirstOrDefault(c => c.Uuid == CH03_UUID);
        _ch04 = chars.Characteristics.FirstOrDefault(c => c.Uuid == CH04_UUID);
        _ch02 = chars.Characteristics.FirstOrDefault(c => c.Uuid == CH02_UUID);
        _ch06 = chars.Characteristics.FirstOrDefault(c => c.Uuid == CH06_UUID);
        _ch102 = chars.Characteristics.FirstOrDefault(c => c.Uuid == CH102_UUID);

        _logger.LogInfo($"CH03 (Tx): {(_ch03 != null ? "✅" : "❌")}  CH04 (Rx): {(_ch04 != null ? "✅" : "❌")}  CH02 (Evt): {(_ch02 != null ? "✅" : "❌")}");
        return _ch03 != null && _ch04 != null;
    }

    private async Task ReadDeviceInfoAsync()
    {
        if (_ch06 == null) return;
        try
        {
            var result = await _ch06.ReadValueAsync();
            if (result.Status == GattCommunicationStatus.Success)
            {
                var reader = DataReader.FromBuffer(result.Value);
                byte[] data = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(data);
                _logger.Log("ch06_read", "rx", "CH06", data, "Device info");
            }
        }
        catch { /* optional */ }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static byte[] BuildPubKey65(ECParameters p)
    {
        byte[] k = new byte[65]; k[0] = 0x04;
        Array.Copy(p.Q.X!, 0, k, 1, 32);
        Array.Copy(p.Q.Y!, 0, k, 33, 32);
        return k;
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        int len = arrays.Sum(a => a.Length);
        byte[] r = new byte[len]; int off = 0;
        foreach (var a in arrays) { Array.Copy(a, 0, r, off, a.Length); off += a.Length; }
        return r;
    }
}