using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace ZwiftClickV2.Bridge.BLE;

/// <summary>
/// Gestiona la conexión BLE con el dispositivo Zwift Click V2.
/// Escanea, conecta, y provee acceso a características GATT.
/// </summary>
public class BleDeviceManager
{
    private BluetoothLEDevice? _device;
    private GattSession? _session;

    // UUIDs Zwift — ENLAZAR SIEMPRE POR UUID, NUNCA POR HANDLE (los handles ATT no son estables).
    //
    // El servicio ZAP propietario que expone CH02/03/04/100/101/102 es 00000001-19CA-...
    // (handle vivo observado 0x0056). El servicio 0xFC82 es un wrapper "Zwift Ride" SECUNDARIO,
    // no el que contiene estas características — ver docs/protocol/ZAP_STATE_OF_THE_ART.md.
    public static readonly Guid ZWIFT_SERVICE_UUID = Guid.Parse("00000001-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid ZWIFT_RIDE_WRAPPER_SERVICE_UUID = Guid.Parse("0000fc82-0000-1000-8000-00805f9b34fb");
    public static readonly Guid CH02_UUID = Guid.Parse("00000002-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH03_UUID = Guid.Parse("00000003-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH04_UUID = Guid.Parse("00000004-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH100_UUID = Guid.Parse("00000100-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH101_UUID = Guid.Parse("00000101-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH102_UUID = Guid.Parse("00000102-19ca-4651-86e5-fa29dcdd09d1");
    // CH06 (00000006-19CA-...) NO existe en el firmware del Click V2 (REFUTADO). El binario de la
    // app declara el UUID pero el dispositivo no lo expone. No leer ni enlazar esta característica.

    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    /// <summary>
    /// Escanea dispositivos BLE cuyo nombre CONTENGA <paramref name="deviceName"/> (sin distinguir
    /// mayúsculas) y se conecta al primero. Con <paramref name="deviceName"/> = "Zwift" reconoce
    /// Click, Play y Ride. <paramref name="onNewDeviceSeen"/> recibe, una sola vez cada uno, los
    /// nombres de dispositivos anunciándose cerca — útil para diagnosticar por qué no aparece el mando.
    /// </summary>
    public async Task<BluetoothLEDevice?> ConnectAsync(string deviceName = "Zwift", TimeSpan? timeout = null,
        Action<string>? onNewDeviceSeen = null)
    {
        timeout ??= TimeSpan.FromSeconds(30);
        Console.WriteLine($"🔍 Escaneando '{deviceName}' ({timeout.Value.TotalSeconds:F0}s)...");

        var tcs = new TaskCompletionSource<ulong>();
        using var cts = new CancellationTokenSource(timeout.Value);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenLock = new object();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (s, args) =>
        {
            string name = args.Advertisement.LocalName;
            if (string.IsNullOrWhiteSpace(name)) return;

            bool isNew;
            lock (seenLock) { isNew = seen.Add(name); }
            if (isNew)
            {
                Console.WriteLine($"   📡 Visto: {name}");
                onNewDeviceSeen?.Invoke(name);
            }

            if (name.Contains(deviceName, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"   ✅ Encontrado: {name} (0x{args.BluetoothAddress:X})");
                tcs.TrySetResult(args.BluetoothAddress);
            }
        };

        cts.Token.Register(() => tcs.TrySetCanceled());
        watcher.Start();

        ulong address;
        try { address = await tcs.Task; }
        catch (OperationCanceledException)
        {
            Console.WriteLine("   ❌ Timeout");
            return null;
        }
        finally { watcher.Stop(); }

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (_device == null)
        {
            Console.WriteLine("   ❌ No se pudo abrir el dispositivo desde su dirección.");
            return null;
        }
        Console.WriteLine($"   📡 Conectado: {_device.Name} (Paired: {_device.DeviceInformation.Pairing.IsPaired})");

        // Forzar/mantener la conexión GATT. FromBluetoothAddressAsync NO conecta por sí solo: la
        // conexión es perezosa y se establece en la primera operación GATT. Abrir una GattSession con
        // MaintainConnection=true pide al stack de Windows que conecte y mantenga el enlace, lo que
        // hace que el descubrimiento de servicios sea fiable (sin esto, el primer GetGattServices
        // suele llegar antes de que el servicio propietario esté enumerado → "servicio no encontrado").
        try
        {
            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
            _session.MaintainConnection = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ⚠️ No se pudo abrir GattSession (se continuará igualmente): {ex.Message}");
        }

        return _device;
    }

    /// <summary>
    /// Obtiene un servicio GATT por UUID, de forma ROBUSTA: lo pide sin caché, comprueba el estado
    /// y REINTENTA mientras la conexión se establece (el descubrimiento GATT en Windows es perezoso
    /// y el servicio propietario puede tardar en aparecer). <paramref name="onDiagnostic"/> recibe
    /// mensajes de diagnóstico (p. ej. los servicios que sí se ven) para mostrarlos en el registro.
    /// </summary>
    public async Task<GattDeviceService?> GetServiceAsync(Guid serviceUuid, Action<string>? onDiagnostic = null)
    {
        if (_device == null) throw new InvalidOperationException("Not connected");

        const int maxAttempts = 8;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // (a) Camino directo: pedir EXACTAMENTE ese UUID, sin caché. Funciona si el servicio
            //     es PRIMARIO. (En el Click V2 sobre Zwift Ride el ZAP es SECUNDARIO, ver (c).)
            try
            {
                var direct = await _device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached);
                if (direct.Status == GattCommunicationStatus.Success && direct.Services.Count > 0)
                {
                    Console.WriteLine($"   ✅ Servicio ZAP encontrado por UUID/primario (intento {attempt}).");
                    return direct.Services[0];
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠️ Intento {attempt}/{maxAttempts}: GetGattServicesForUuid: {ex.Message}");
            }

            // (b) Enumerar servicios PRIMARIOS (uncached). Búsqueda directa + diagnóstico.
            GattDeviceService[] primaries = Array.Empty<GattDeviceService>();
            try
            {
                var all = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                if (all.Status == GattCommunicationStatus.Success && all.Services != null)
                {
                    primaries = all.Services.ToArray();
                    var match = primaries.FirstOrDefault(s => s.Uuid == serviceUuid);
                    if (match != null)
                    {
                        Console.WriteLine($"   ✅ Servicio ZAP encontrado al enumerar primarios (intento {attempt}).");
                        return match;
                    }

                    string uuids = string.Join(", ", primaries.Select(s => Short(s.Uuid)));
                    Console.WriteLine($"   …intento {attempt}/{maxAttempts}: {primaries.Length} primarios, sin el ZAP. [{uuids}]");
                    if (attempt == 1 || attempt == maxAttempts)
                        onDiagnostic?.Invoke($"Servicios primarios ({primaries.Length}): {uuids}");
                }
                else
                {
                    Console.WriteLine($"   …intento {attempt}/{maxAttempts}: estado {all.Status}.");
                    if (attempt == 1)
                        onDiagnostic?.Invoke($"El mando aún no responde a la lista de servicios (estado {all.Status}). Reintentando…");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠️ Intento {attempt}/{maxAttempts}: GetGattServices: {ex.Message}");
            }

            // (c) CLAVE para el Click V2 sobre Zwift Ride: el servicio ZAP está ANIDADO como
            //     SECUNDARIO dentro de otro (típicamente 0xFC82). WinRT no lo devuelve entre los
            //     primarios → hay que recorrer los INCLUDED services de cada primario.
            foreach (var parent in primaries)
            {
                try
                {
                    var inc = await parent.GetIncludedServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached);
                    if (inc.Status == GattCommunicationStatus.Success && inc.Services.Count > 0)
                    {
                        Console.WriteLine($"   ✅ Servicio ZAP encontrado ANIDADO dentro de {Short(parent.Uuid)} (intento {attempt}).");
                        onDiagnostic?.Invoke($"Servicio ZAP localizado dentro de {Short(parent.Uuid)} (servicio incluido).");
                        return inc.Services[0];
                    }

                    // Diagnóstico: listar los incluidos que sí se ven (solo una vez).
                    if (attempt == 1)
                    {
                        var allInc = await parent.GetIncludedServicesAsync(BluetoothCacheMode.Uncached);
                        if (allInc.Status == GattCommunicationStatus.Success && allInc.Services.Count > 0)
                        {
                            string sub = string.Join(", ", allInc.Services.Select(s => Short(s.Uuid)));
                            Console.WriteLine($"      ↳ incluidos en {Short(parent.Uuid)}: {sub}");
                            onDiagnostic?.Invoke($"Dentro de {Short(parent.Uuid)}: {sub}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ⚠️ included de {Short(parent.Uuid)}: {ex.Message}");
                }
            }

            await Task.Delay(800); // dar tiempo a que la conexión GATT se establezca/enumere
        }

        return null;
    }

    /// <summary>Forma corta de un UUID para diagnóstico: el grupo significativo o el UUID entero.</summary>
    private static string Short(Guid g)
    {
        string s = g.ToString();
        // UUIDs base de 16 bits (0000XXXX-0000-1000-8000-00805f9b34fb) → "0xXXXX".
        if (s.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase))
            return "0x" + s.Substring(4, 4);
        // UUIDs propietarios Zwift (0000000X-19ca-…) → "ZAP:000000X".
        if (s.Contains("19ca-4651-86e5-fa29dcdd09d1", StringComparison.OrdinalIgnoreCase))
            return "ZAP:" + s.Substring(0, 8);
        return s.Substring(0, 8) + "…";
    }

    /// <summary>
    /// Obtiene una característica GATT por UUID.
    /// </summary>
    public async Task<GattCharacteristic?> GetCharacteristicAsync(Guid serviceUuid, Guid characteristicUuid)
    {
        var service = await GetServiceAsync(serviceUuid);
        if (service == null) return null;
        var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        return chars.Characteristics.FirstOrDefault(c => c.Uuid == characteristicUuid);
    }

    /// <summary>
    /// Lee el valor de una característica GATT.
    /// </summary>
    public async Task<byte[]?> ReadCharacteristicAsync(Guid serviceUuid, Guid characteristicUuid)
    {
        var ch = await GetCharacteristicAsync(serviceUuid, characteristicUuid);
        if (ch == null) return null;
        var result = await ch.ReadValueAsync();
        if (result.Status != GattCommunicationStatus.Success) return null;

        using var reader = Windows.Storage.Streams.DataReader.FromBuffer(result.Value);
        byte[] data = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(data);
        return data;
    }

    /// <summary>
    /// Desconecta y libera el dispositivo.
    /// </summary>
    public void Disconnect()
    {
        _session?.Dispose();
        _session = null;
        _device?.Dispose();
        _device = null;
    }
}
