using System.Security.Cryptography;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using ZwiftClickV2.Bridge.Crypto;

namespace ZwiftClickV2.Bridge.Protocol;

/// <summary>
/// Prueba diferentes formatos de handshake para descubrir cuál acepta el Zwift Click V2.
/// Itera sobre 6 variantes de "RideOn" + sufijos + clave pública ECDH,
/// detectando automáticamente si la respuesta es una clave pública EC válida
/// o un mensaje de rechazo (datos de batería/estado).
/// </summary>
public class HandshakeProber
{
    private static readonly byte[] RIDE_ON = "RideOn"u8.ToArray();

    // ── UUIDs de servicio y características Zwift ──────────────────────
    // V1/V2 comparten el mismo servicio
    private static readonly Guid ZWIFT_SERVICE_UUID  = Guid.Parse("0000fc82-0000-1000-8000-00805f9b34fb");
    private static readonly Guid CH02_UUID = Guid.Parse("00000002-19ca-4651-86e5-fa29dcdd09d1");
    private static readonly Guid CH03_UUID = Guid.Parse("00000003-19ca-4651-86e5-fa29dcdd09d1");
    private static readonly Guid CH04_UUID = Guid.Parse("00000004-19ca-4651-86e5-fa29dcdd09d1");
    private static readonly Guid CH100_UUID = Guid.Parse("00000100-19ca-4651-86e5-fa29dcdd09d1");
    private static readonly Guid CH101_UUID = Guid.Parse("00000101-19ca-4651-86e5-fa29dcdd09d1");
    private static readonly Guid CH102_UUID = Guid.Parse("00000102-19ca-4651-86e5-fa29dcdd09d1");

    // ── Formatos de handshake ──────────────────────────────────────────

    public enum HandshakeFormat
    {
        V1_WithRideOn_01_02,    // "RideOn" + 01 02 + pubkey[65]
        V1_WithRideOn_00_09,    // "RideOn" + 00 09 + pubkey[65]
        V2_BarePublicKey,       // Solo pubkey[65] sin prefijos
        V2_RideOn_02_03,        // "RideOn" + 02 03 + pubkey[65]
        V2_RideOn_Plus04Key,    // "RideOn" + 04 + pubkey[65]
        V2_RideOn_02_03_Key64,  // "RideOn" + 02 03 + pubkey[64] (sin 0x04)
    }

    /// <summary>
    /// Construye el payload del handshake según el formato especificado.
    /// </summary>
    public static byte[] BuildHandshake(HandshakeFormat format, byte[] ourPublicKey65)
    {
        return format switch
        {
            HandshakeFormat.V1_WithRideOn_01_02  => Concat(RIDE_ON, new byte[] { 0x01, 0x02 }, ourPublicKey65),
            HandshakeFormat.V1_WithRideOn_00_09  => Concat(RIDE_ON, new byte[] { 0x00, 0x09 }, ourPublicKey65),
            HandshakeFormat.V2_BarePublicKey     => ourPublicKey65,
            HandshakeFormat.V2_RideOn_02_03      => Concat(RIDE_ON, new byte[] { 0x02, 0x03 }, ourPublicKey65),
            HandshakeFormat.V2_RideOn_Plus04Key  => Concat(RIDE_ON, new byte[] { 0x04 }, ourPublicKey65),
            HandshakeFormat.V2_RideOn_02_03_Key64=> Concat(RIDE_ON, new byte[] { 0x02, 0x03 }, ourPublicKey65.Skip(1).ToArray()),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    /// <summary>
    /// Determina si una respuesta parece ser una clave pública EC P-256 válida.
    /// Busca el prefijo 0x04 seguido de 64 bytes con alta entropía
    /// y valida que el punto pertenezca a la curva.
    /// </summary>
    public static bool LooksLikeValidPublicKey(byte[] response)
    {
        if (response == null || response.Length < 65) return false;

        // Buscar 0x04 + 64 bytes de alta entropía dentro de la respuesta
        byte[] keyData = ExtractPublicKeyCandidate(response);
        if (keyData == null) return false;

        // Validar entropía: al menos 40 de los 64 bytes XY deben ser no-cero
        byte[] xyBytes = keyData.Skip(1).ToArray();
        int nonZero = xyBytes.Count(b => b != 0);
        if (nonZero < 40) return false;

        // Validar como punto de curva P-256
        try
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var ecParams = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = xyBytes.Take(32).ToArray(),
                    Y = xyBytes.Skip(32).Take(32).ToArray()
                }
            };
            ecdh.ImportParameters(ecParams);
            ecdh.ExportParameters(false); // Valida el punto en la curva
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determina si la respuesta es un mensaje de rechazo conocido
    /// (datos de batería/estado en lugar de clave pública).
    /// </summary>
    public static bool LooksLikeRejection(byte[] response)
    {
        if (response == null || response.Length < 10) return false;

        // Patrón: "RideOn" + 02 03 + datos protobuf
        if (response.Length >= 8 &&
            response[0] == 'R' && response[1] == 'i' && response[2] == 'd' &&
            response[3] == 'e' && response[4] == 'O' && response[5] == 'n' &&
            response[6] == 0x02 && response[7] == 0x03)
        {
            byte[] afterHeader = response.Skip(8).ToArray();

            // Batería: 0x10 = field 2 varint, 0x64 = 100%
            if (afterHeader.Length >= 2 && afterHeader[0] == 0x10 && afterHeader[1] == 0x64)
                return true;

            // Código de estado 0x58 = field 11 varint
            if (afterHeader.Length >= 2 && afterHeader[0] == 0x58)
                return true;

            // Cualquier protobuf con fields bajos (0x08, 0x10, 0x18, 0x20)
            if (afterHeader.Length >= 1 &&
                (afterHeader[0] == 0x08 || afterHeader[0] == 0x10 ||
                 afterHeader[0] == 0x18 || afterHeader[0] == 0x20))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Extrae una clave pública EC candidata (0x04 + 64 bytes) de una respuesta.
    /// </summary>
    public static byte[]? ExtractPublicKeyCandidate(byte[] response)
    {
        for (int offset = 0; offset <= response.Length - 65; offset++)
        {
            if (response[offset] == 0x04)
            {
                byte[] candidate = response.Skip(offset).Take(65).ToArray();
                // Verificación rápida de entropía antes de validar curva
                int nonZero = candidate.Skip(1).Count(b => b != 0);
                if (nonZero >= 40)
                    return candidate;
            }
        }
        return null;
    }

    // ── BLE Scanning & Connection ──────────────────────────────────────

    /// <summary>
    /// Escanea dispositivos BLE buscando "Zwift Click" y se conecta al primero encontrado.
    /// </summary>
    public static async Task<BluetoothLEDevice?> ScanAndConnectAsync(TimeSpan timeout)
    {
        Console.WriteLine($"🔍 Buscando 'Zwift Click' ({timeout.TotalSeconds:F0}s)...");

        var tcs = new TaskCompletionSource<ulong>();
        using var cts = new CancellationTokenSource(timeout);

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (s, args) =>
        {
            if (!string.IsNullOrEmpty(args.Advertisement.LocalName) &&
                args.Advertisement.LocalName.StartsWith("Zwift Click", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"   ✅ Encontrado: {args.Advertisement.LocalName} (0x{args.BluetoothAddress:X})");
                tcs.TrySetResult(args.BluetoothAddress);
            }
        };

        cts.Token.Register(() => tcs.TrySetCanceled());
        watcher.Start();

        ulong address;
        try
        {
            address = await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("   ❌ Timeout: no se encontró ningún Zwift Click");
            return null;
        }
        finally
        {
            watcher.Stop();
        }

        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        Console.WriteLine($"   📡 Conectado: {device.Name} (Paired: {device.DeviceInformation.Pairing.IsPaired})");
        return device;
    }

    /// <summary>
    /// Obtiene las características GATT necesarias del servicio Zwift.
    /// </summary>
    public static async Task<(GattCharacteristic? ch03, GattCharacteristic? ch04, GattCharacteristic? ch02)>
        GetCharacteristicsAsync(BluetoothLEDevice device)
    {
        var servicesResult = await device.GetGattServicesAsync();
        var service = servicesResult.Services?.FirstOrDefault(s => s.Uuid == ZWIFT_SERVICE_UUID);
        if (service == null)
        {
            Console.WriteLine("   ❌ Servicio Zwift (0xFC82) no encontrado");
            return (null, null, null);
        }

        var charsResult = await service.GetCharacteristicsAsync();
        var allChars = charsResult.Characteristics;

        var ch03 = allChars.FirstOrDefault(c => c.Uuid == CH03_UUID);
        var ch04 = allChars.FirstOrDefault(c => c.Uuid == CH04_UUID);
        var ch02 = allChars.FirstOrDefault(c => c.Uuid == CH02_UUID);

        Console.WriteLine($"   CH03 (write): {(ch03 != null ? "✅" : "❌")}");
        Console.WriteLine($"   CH04 (indicate): {(ch04 != null ? "✅" : "❌")}");
        Console.WriteLine($"   CH02 (notify): {(ch02 != null ? "✅" : "❌")}");

        return (ch03, ch04, ch02);
    }

    /// <summary>
    /// Genera un par de claves ECDH P-256 efímero y retorna la clave pública
    /// en formato uncompressed (65 bytes: 0x04 + X + Y).
    /// </summary>
    public static (ECDiffieHellman key, byte[] publicKey65) GenerateEcdhKeyPair()
    {
        var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdh.ExportParameters(false);

        byte[] pubKey65 = new byte[65];
        pubKey65[0] = 0x04;
        Array.Copy(parameters.Q.X!, 0, pubKey65, 1, 32);
        Array.Copy(parameters.Q.Y!, 0, pubKey65, 33, 32);

        return (ecdh, pubKey65);
    }

    // ── Prober principal ───────────────────────────────────────────────

    /// <summary>
    /// Ejecuta la secuencia completa de probing de handshake.
    /// Prueba los 6 formatos contra el dispositivo y retorna 0 si alguno funciona.
    /// </summary>
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine("  HANDSHAKE PROBER — Zwift Click V2");
        Console.WriteLine("═══════════════════════════════════════════\n");

        // ── 1. Conectar al dispositivo ─────────────────────────────────
        var device = await ScanAndConnectAsync(TimeSpan.FromSeconds(30));
        if (device == null) return 1;

        // ── 2. Obtener características GATT ────────────────────────────
        var (ch03, ch04, ch02) = await GetCharacteristicsAsync(device);
        if (ch03 == null || ch04 == null)
        {
            Console.WriteLine("❌ CH03/CH04 requeridos pero no encontrados. Abortando.");
            return 1;
        }

        // ── 3. Generar par de claves ECDH fresco (uno por intento) ─────
        // NOTA: cada formato prueba con un par nuevo para evitar reutilización
        var formats = Enum.GetValues<HandshakeFormat>();

        // ── 4. Suscribirse a CH04 (Indicate) para recibir respuestas ───
        byte[]? lastResponse = null;
        var responseReceived = new ManualResetEventSlim(false);

        void OnCh04ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            lastResponse = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(lastResponse);
            responseReceived.Set();
        }

        ch04.ValueChanged += OnCh04ValueChanged;
        await ch04.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Indicate);

        // ── 5. Probar cada formato ─────────────────────────────────────
        foreach (var format in formats)
        {
            Console.WriteLine($"\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.WriteLine($"🧪 Probando formato: {format}");

            // Generar par nuevo para cada intento
            using var ourKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var ourParams = ourKey.ExportParameters(false);
            byte[] ourPubKey65 = new byte[65];
            ourPubKey65[0] = 0x04;
            Array.Copy(ourParams.Q.X!, 0, ourPubKey65, 1, 32);
            Array.Copy(ourParams.Q.Y!, 0, ourPubKey65, 33, 32);

            byte[] handshakePayload = BuildHandshake(format, ourPubKey65);
            string preview = handshakePayload.Length <= 60
                ? BitConverter.ToString(handshakePayload).Replace("-", "")
                : BitConverter.ToString(handshakePayload.Take(30).ToArray()).Replace("-", "") + "...";
            Console.WriteLine($"   Payload ({handshakePayload.Length}B): {preview}");

            responseReceived.Reset();
            lastResponse = null;

            // Enviar handshake por CH03 (WriteWithoutResponse)
            var writer = new DataWriter();
            writer.WriteBytes(handshakePayload);
            var status = await ch03.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
            Console.WriteLine($"   Write status: {status}");

            // Esperar respuesta (max 3 segundos)
            bool gotResponse = responseReceived.Wait(TimeSpan.FromSeconds(3));

            if (!gotResponse || lastResponse == null)
            {
                Console.WriteLine($"   ❌ Sin respuesta (timeout 3s)");
                await Task.Delay(300);
                continue;
            }

            Console.WriteLine($"   📩 Respuesta ({lastResponse.Length}B): {BitConverter.ToString(lastResponse).Replace("-", "")}");

            // Analizar respuesta
            if (LooksLikeRejection(lastResponse))
            {
                Console.WriteLine($"   ⚠️  RECHAZO — el dispositivo devolvió datos de estado en lugar de clave pública");
                await Task.Delay(300);
                continue;
            }

            if (LooksLikeValidPublicKey(lastResponse))
            {
                Console.WriteLine($"   🎉 ¡CLAVE PÚBLICA EC VÁLIDA DETECTADA!");

                byte[]? peerPub65 = ExtractPublicKeyCandidate(lastResponse);
                if (peerPub65 == null)
                {
                    Console.WriteLine($"   ❌ No se pudo extraer la clave de la respuesta");
                    await Task.Delay(300);
                    continue;
                }

                Console.WriteLine($"   Peer pubkey (65B): {BitConverter.ToString(peerPub65).Replace("-", "")}");

                // Intentar inicializar ZPEncryption
                try
                {
                    var zp = new ZPEncryptionV2();
                    zp.Initialize(ourKey, peerPub65);

                    Console.WriteLine($"\n   🏆🏆🏆 FORMATO GANADOR: {format} 🏆🏆🏆\n");
                    Console.WriteLine("📡 Escuchando mensajes cifrados en CH02/CH04 (10 segundos)...");

                    await ListenEncryptedMessagesAsync(ch02, ch04, zp, TimeSpan.FromSeconds(10));

                    return 0; // Éxito
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ❌ Error inicializando ZPEncryptionV2: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"   ❓ Respuesta desconocida (no es clave EC ni rechazo conocido)");
            }

            await Task.Delay(500);
        }

        Console.WriteLine("\n❌ Ninguno de los 6 formatos de handshake funcionó.");
        Console.WriteLine("   Posibles causas:");
        Console.WriteLine("   - El dispositivo requiere un token de autenticación previo (DRM diario)");
        Console.WriteLine("   - El formato exacto difiere de las 6 variantes probadas");
        Console.WriteLine("   - El dispositivo está en modo 'locked' y necesita unlock primero");
        return 1;
    }

    // ── Listener de mensajes cifrados ──────────────────────────────────

    private static async Task ListenEncryptedMessagesAsync(
        GattCharacteristic? ch02,
        GattCharacteristic ch04,
        ZPEncryptionV2 zp,
        TimeSpan duration)
    {
        int msgCount = 0;
        var cts = new CancellationTokenSource(duration);
        var tcs = new TaskCompletionSource<bool>();
        cts.Token.Register(() => tcs.TrySetResult(true));

        void OnNotification(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            try
            {
                var reader = DataReader.FromBuffer(args.CharacteristicValue);
                var data = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(data);

                string chLabel = sender.Uuid == CH02_UUID ? "CH02" :
                                 sender.Uuid == CH04_UUID ? "CH04" :
                                 sender.Uuid.ToString("N")[..8];
                Console.WriteLine($"   📨 [{chLabel}] ({data.Length}B): {BitConverter.ToString(data).Replace("-", "")}");

                try
                {
                    byte[] plaintext = zp.Decrypt(data);
                    string ascii = Encoding.ASCII.GetString(plaintext.Where(b => b >= 32 && b < 127).ToArray());
                    Console.WriteLine($"      ✅ PLAINTEXT: {BitConverter.ToString(plaintext).Replace("-", "")}");
                    if (ascii.Length > 0)
                        Console.WriteLine($"      📝 ASCII: \"{ascii}\"");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"      ❌ Decrypt falló: {ex.Message}");
                }
                msgCount++;
            }
            catch { /* ignore parsing errors */ }
        }

        ch04.ValueChanged += OnNotification;
        if (ch02 != null)
        {
            ch02.ValueChanged += OnNotification;
            await ch02.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
        }

        Console.WriteLine($"   ⏳ Esperando {duration.TotalSeconds:F0}s...\n");
        await tcs.Task;

        ch04.ValueChanged -= OnNotification;
        if (ch02 != null) ch02.ValueChanged -= OnNotification;

        Console.WriteLine($"\n   📊 Total mensajes recibidos: {msgCount}");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static byte[] Concat(params byte[][] arrays)
    {
        int totalLen = arrays.Sum(a => a.Length);
        byte[] result = new byte[totalLen];
        int offset = 0;
        foreach (var arr in arrays)
        {
            Array.Copy(arr, 0, result, offset, arr.Length);
            offset += arr.Length;
        }
        return result;
    }
}