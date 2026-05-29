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
/// Fuzzer sistemático para descubrir el header correcto de 7 bytes del handshake V2.
/// 
/// Formato esperado por el dispositivo (según Ghidra decompilación):
///   [Header 7 bytes] [0x04 forzado] [EC Public Key 64 bytes (X + Y)]
///   Total: 72 bytes
/// 
/// NOTA: El código decompilado muestra `*puStack_110 = 4;` forzando el byte 0x04
/// en la posición 7 del mensaje. Por tanto el formato real es:
///   header[7] + pubkey[65]  (donde pubkey[0] ya es 0x04)
/// 
/// Escribe en CH03 (WriteWithoutResponse) y espera respuesta en CH04 (Indicate).
/// </summary>
public class HandshakeFuzzerV2
{
    private static readonly Guid ZWIFT_SERVICE_UUID = Guid.Parse("0000fc82-0000-1000-8000-00805f9b34fb");
    private static readonly Guid CH03_UUID = Guid.Parse("00000003-19ca-4651-86e5-fa29dcdd09d1"); // SyncRx — escribimos aquí
    private static readonly Guid CH04_UUID = Guid.Parse("00000004-19ca-4651-86e5-fa29dcdd09d1"); // SyncTx — recibimos aquí (Indicate)
    private static readonly Guid CH02_UUID = Guid.Parse("00000002-19ca-4651-86e5-fa29dcdd09d1");
    private static readonly Guid CH06_UUID = Guid.Parse("00000006-19ca-4651-86e5-fa29dcdd09d1");

    // Headers de 7 bytes a probar
    private static readonly Dictionary<string, byte[]> HEADERS_TO_TEST = new()
    {
        { "RideOn+0x00", Encoding.ASCII.GetBytes("RideOn").Concat(new byte[] { 0x00 }).ToArray() },
        { "RideOn+0x01", Encoding.ASCII.GetBytes("RideOn").Concat(new byte[] { 0x01 }).ToArray() },
        { "RideOn+0x02", Encoding.ASCII.GetBytes("RideOn").Concat(new byte[] { 0x02 }).ToArray() },
        { "RideOn+0x03", Encoding.ASCII.GetBytes("RideOn").Concat(new byte[] { 0x03 }).ToArray() },
        { "RideOn+0x04", Encoding.ASCII.GetBytes("RideOn").Concat(new byte[] { 0x04 }).ToArray() },
        { "AllZeros", new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 } },
        { "Sequence1-7", new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 } },
        { "AllFF", new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF } },
        { "Version1", new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 } },
        { "Version2", new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 } },
    };

    private readonly StructuredLogger _logger;
    private readonly List<FuzzV2Result> _results = new();
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _ch03, _ch04, _ch02, _ch06;

    public HandshakeFuzzerV2(StructuredLogger logger)
    {
        _logger = logger;
    }

    public record FuzzV2Result(
        string HeaderName,
        byte[] HeaderBytes,
        byte[] Payload,
        byte[]? Response,
        string Status,
        string Notes,
        byte[]? PeerPublicKey
    );

    /// <summary>
    /// Ejecuta el fuzzer V2 completo: conectar, probar los 10 headers,
    /// detectar automáticamente si alguno produce un handshake exitoso.
    /// </summary>
    public async Task<List<FuzzV2Result>> RunAsync()
    {
        _logger.LogInfo("═══════════════════════════════════════════");
        _logger.LogInfo("  HANDSHAKE V2 FUZZER — Header Discovery");
        _logger.LogInfo("═══════════════════════════════════════════");
        _logger.LogInfo($"");
        _logger.LogInfo($"Probando {HEADERS_TO_TEST.Count} formatos de header de 7 bytes");
        _logger.LogInfo($"Formato del mensaje: header[7] + pubkey[65] = 72 bytes");
        _logger.LogInfo($"");

        // 1. Conectar al dispositivo
        if (!await ConnectAsync()) return _results;

        // 2. Leer CH06 (info del dispositivo)
        await ReadDeviceInfoAsync();

        // 3. Suscribirse a CH04 para recibir respuestas (Indicate)
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
            _logger.LogInfo("✅ CH04 suscrito (Indicate) para recibir respuestas");
        }

        // 4. Probar cada header
        foreach (var (headerName, headerBytes) in HEADERS_TO_TEST)
        {
            _logger.LogInfo($"");
            _logger.LogInfo($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            _logger.LogInfo($"🧪 TEST: {headerName}");
            _logger.LogInfo($"   Header ({headerBytes.Length}B): {BitConverter.ToString(headerBytes).Replace("-", " ")}");

            responseEvent.Reset();
            lastResponse = null;

            // Generar par ECDH fresco para cada intento
            using var ourKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var ourParams = ourKey.ExportParameters(false);
            byte[] pubKey65 = BuildPubKey65(ourParams);

            // Construir mensaje: header[7] + pubkey[65] = 72 bytes
            byte[] handshakeMessage = new byte[72];
            Array.Copy(headerBytes, 0, handshakeMessage, 0, 7);
            Array.Copy(pubKey65, 0, handshakeMessage, 7, 65);

            string preview = BitConverter.ToString(handshakeMessage.Take(16).ToArray()).Replace("-", "") + "...";
            _logger.LogInfo($"   Payload (72B): {preview}");
            _logger.Log("handshake_v2_sent", "tx", "CH03", handshakeMessage, headerName);

            // Enviar a CH03
            var writer = new DataWriter();
            writer.WriteBytes(handshakeMessage);
            var writeStatus = await _ch03!.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);

            if (writeStatus != GattCommunicationStatus.Success)
            {
                _logger.LogInfo($"   ⚠️ Write falló: {writeStatus}");
                _results.Add(new FuzzV2Result(headerName, headerBytes, handshakeMessage, null, "WRITE_FAILED", $"GattCommunicationStatus: {writeStatus}", null));
                await Task.Delay(1000);
                continue;
            }

            _logger.LogInfo($"   ✅ Handshake enviado, esperando respuesta...");

            // Esperar respuesta (3s)
            bool gotResponse = responseEvent.Wait(TimeSpan.FromSeconds(3));
            await Task.Delay(1000); // Gap entre intentos

            if (!gotResponse || lastResponse == null)
            {
                _logger.LogInfo($"   ❌ Timeout (3s) — sin respuesta");
                _results.Add(new FuzzV2Result(headerName, headerBytes, handshakeMessage, null, "NO_RESPONSE", "Timeout 3s", null));
                continue;
            }

            _logger.Log("handshake_v2_response", "rx", "CH04", lastResponse, headerName);
            _logger.LogInfo($"   📩 Respuesta ({lastResponse.Length}B): {BitConverter.ToString(lastResponse).Replace("-", " ")}");

            // Clasificar respuesta usando HandshakeParser existente
            var classification = HandshakeParser.Classify(lastResponse);
            byte[]? peerPubKey = null;
            string status;
            string notes;

            if (classification == HandshakeParser.ResponseType.PublicKey)
            {
                peerPubKey = HandshakeParser.ExtractPublicKey(lastResponse);
                status = "SUCCESS";
                notes = "PUBLIC_KEY_DETECTED";

                if (peerPubKey != null)
                {
                    _logger.LogInfo($"   🎉 ¡CLAVE PÚBLICA EC VÁLIDA!");
                    _logger.LogInfo($"   Peer pubkey (65B): {BitConverter.ToString(peerPubKey.Take(16).ToArray()).Replace("-", "")}...");

                    // Intentar inicializar cifrado
                    try
                    {
                        var zp = new ZPEncryptionV2();
                        zp.Initialize(ourKey, peerPubKey, saltPublicKey65: peerPubKey);
                        _logger.LogInfo("   ✅ ZPEncryptionV2 inicializado correctamente");
                        notes += " | ENCRYPTION_OK";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"   ❌ ZPEncryptionV2 falló: {ex.Message}");
                        notes += " | ENCRYPTION_FAILED";
                    }
                }
            }
            else if (classification == HandshakeParser.ResponseType.StatusMessage)
            {
                status = "REJECTED";
                string errorHex = BitConverter.ToString(lastResponse.Take(20).ToArray()).Replace("-", "");
                notes = $"REJECTION: {errorHex}";

                // Detectar código de error específico
                if (lastResponse.Length >= 10)
                {
                    byte[] afterRideOn = lastResponse.Skip(8).ToArray();
                    if (afterRideOn.Length >= 2)
                    {
                        if (afterRideOn[0] == 0x58)
                            notes = $"Error 58 {afterRideOn[1]:X2} (Field 11, value {afterRideOn[1]}) — posible handshake inválido";
                        else if (afterRideOn[0] == 0xC0)
                            notes = $"Error C0 {afterRideOn[1]:X2} (Field 24, value {afterRideOn[1]}) — posible formato incorrecto";
                        else if (afterRideOn[0] == 0x10 && afterRideOn.Length >= 2)
                            notes = $"Datos de batería (Field 2, value {afterRideOn[1]}) — handshake rechazado";
                        else if (afterRideOn[0] == 0x12)
                            notes = $"Estructura anidada (Field 2 length-delimited) — error estructurado";
                    }
                }
                _logger.LogInfo($"   ⚠️ RECHAZO: {notes}");
            }
            else
            {
                status = "UNKNOWN";
                notes = $"Respuesta desconocida ({lastResponse.Length}B)";
                _logger.LogInfo($"   ❓ RESPUESTA DESCONOCIDA: {BitConverter.ToString(lastResponse.Take(16).ToArray()).Replace("-", "")}...");
            }

            _results.Add(new FuzzV2Result(headerName, headerBytes, handshakeMessage, lastResponse, status, notes, peerPubKey));
        }

        // 5. Resumen final
        _logger.LogInfo($"");
        _logger.LogInfo("═══════════════════════════════════════════");
        _logger.LogInfo("  RESUMEN DEL FUZZER V2");
        _logger.LogInfo("═══════════════════════════════════════════");
        _logger.LogInfo($"  Total headers probados: {_results.Count}");
        _logger.LogInfo($"  SUCCESS (clave pública): {_results.Count(r => r.Status == "SUCCESS")}");
        _logger.LogInfo($"  REJECTED (error): {_results.Count(r => r.Status == "REJECTED")}");
        _logger.LogInfo($"  NO_RESPONSE (timeout): {_results.Count(r => r.Status == "NO_RESPONSE")}");
        _logger.LogInfo($"  UNKNOWN: {_results.Count(r => r.Status == "UNKNOWN")}");

        var winner = _results.FirstOrDefault(r => r.Status == "SUCCESS");
        if (winner != null)
        {
            _logger.LogInfo($"");
            _logger.LogInfo($"🏆🏆🏆 HEADER GANADOR: {winner.HeaderName} 🏆🏆🏆");
            _logger.LogInfo($"   Header bytes: {BitConverter.ToString(winner.HeaderBytes).Replace("-", " ")}");
            _logger.LogInfo($"   Notas: {winner.Notes}");
        }
        else
        {
            _logger.LogInfo($"");
            _logger.LogInfo($"❌ Ningún header produjo un handshake exitoso.");
            _logger.LogInfo($"   Posibles causas:");
            _logger.LogInfo($"   - El dispositivo requiere DRM diario (desbloqueo con Zwift oficial)");
            _logger.LogInfo($"   - El header real es diferente a los 10 probados");
            _logger.LogInfo($"   - Se necesita capturar tráfico BLE de Zwift oficial con sniffer hardware");
        }

        return _results;
    }

    // ── BLE Connection ──────────────────────────────────────────────────

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
            _logger.LogError("❌ Timeout: no se encontró ningún Zwift Click");
            return false;
        }
        finally { watcher.Stop(); }

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        _logger.LogInfo($"📡 Conectado: {_device.Name}");

        var servicesResult = await _device.GetGattServicesAsync();
        var service = servicesResult.Services?.FirstOrDefault(s => s.Uuid == ZWIFT_SERVICE_UUID);
        if (service == null)
        {
            _logger.LogError("❌ Servicio Zwift (0xFC82) no encontrado");
            return false;
        }

        var chars = await service.GetCharacteristicsAsync();
        _ch03 = chars.Characteristics.FirstOrDefault(c => c.Uuid == CH03_UUID);
        _ch04 = chars.Characteristics.FirstOrDefault(c => c.Uuid == CH04_UUID);
        _ch02 = chars.Characteristics.FirstOrDefault(c => c.Uuid == CH02_UUID);
        _ch06 = chars.Characteristics.FirstOrDefault(c => c.Uuid == CH06_UUID);

        _logger.LogInfo($"CH03 (write): {(_ch03 != null ? "✅" : "❌")}  CH04 (indicate): {(_ch04 != null ? "✅" : "❌")}  CH02 (notify): {(_ch02 != null ? "✅" : "❌")}");

        if (_ch03 == null || _ch04 == null)
        {
            _logger.LogError("❌ CH03 y/o CH04 no encontrados. No se puede continuar.");
            return false;
        }

        return true;
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
                _logger.Log("ch06_read", "rx", "CH06", data, "DeviceInfo");
                _logger.LogInfo($"   📋 CH06 ({data.Length}B): {BitConverter.ToString(data).Replace("-", " ")}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogInfo($"   ⚠️ No se pudo leer CH06: {ex.Message}");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static byte[] BuildPubKey65(ECParameters p)
    {
        byte[] k = new byte[65];
        k[0] = 0x04;
        Array.Copy(p.Q.X!, 0, k, 1, 32);
        Array.Copy(p.Q.Y!, 0, k, 33, 32);
        return k;
    }
}