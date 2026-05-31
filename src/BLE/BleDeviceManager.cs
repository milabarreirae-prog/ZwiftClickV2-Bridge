using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace ZwiftClickV2.Bridge.BLE;

/// <summary>Las características de control del servicio ZAP, ya resueltas.</summary>
public sealed class ZapCharacteristics
{
    public required GattCharacteristic Ch02 { get; init; } // Async / Notify (stream del dispositivo)
    public required GattCharacteristic Ch03 { get; init; } // SyncRx / Write (comandos host→device)
    public GattCharacteristic? Ch04 { get; init; }         // SyncTx / Indicate (eco/estado)
    /// <summary>TODAS las características descubiertas, por UUID (incluye CH100/101/102 si existen).</summary>
    public required IReadOnlyDictionary<Guid, GattCharacteristic> All { get; init; }

    // El Click V2 son DOS mandos puenteados → el dispositivo puede exponer DOS instancias del
    // servicio ZAP (00000001-19CA…), cada una con su propia CH02 (notify) y CH03 (write). En la
    // captura de la app oficial, la telemetría/batería sale por UNA instancia y los BOTONES por la
    // OTRA. El deduplicado por UUID (Ch02/Ch03) se queda con la primera; estas listas guardan TODAS.
    public IReadOnlyList<GattCharacteristic> Ch02All { get; init; } = Array.Empty<GattCharacteristic>();
    public IReadOnlyList<GattCharacteristic> Ch03All { get; init; } = Array.Empty<GattCharacteristic>();
}

/// <summary>
/// Gestiona la conexión BLE con el dispositivo Zwift Click V2.
/// Escanea, conecta, empareja si hace falta, y resuelve las características GATT.
/// </summary>
public class BleDeviceManager
{
    private BluetoothLEDevice? _device;
    private GattSession? _session;

    /// <summary>Se dispara cuando cambia el estado de conexión BLE (true = conectado).</summary>
    public event Action<bool>? ConnectionChanged;

    // UUIDs Zwift — ENLAZAR SIEMPRE POR UUID, NUNCA POR HANDLE (los handles ATT no son estables).
    // El servicio ZAP propietario que expone CH02/03/04/100/101/102 es 00000001-19CA-…
    // En captura en vivo, CH02/03/04 están en handles bajos (0x001B/1E/20) dentro de ese servicio;
    // 0xFC82 (Zwift Ride) es un grupo SEPARADO. Buscamos las CARACTERÍSTICAS por UUID estén donde estén.
    public static readonly Guid ZWIFT_SERVICE_UUID = Guid.Parse("00000001-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid ZWIFT_RIDE_WRAPPER_SERVICE_UUID = Guid.Parse("0000fc82-0000-1000-8000-00805f9b34fb");
    public static readonly Guid CH02_UUID = Guid.Parse("00000002-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH03_UUID = Guid.Parse("00000003-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH04_UUID = Guid.Parse("00000004-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH100_UUID = Guid.Parse("00000100-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH101_UUID = Guid.Parse("00000101-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH102_UUID = Guid.Parse("00000102-19ca-4651-86e5-fa29dcdd09d1");

    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    /// <summary>
    /// Escanea dispositivos BLE cuyo nombre CONTENGA <paramref name="deviceName"/> y se conecta al
    /// primero. <paramref name="onNewDeviceSeen"/> recibe, una vez cada uno, los nombres vistos cerca.
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

        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

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

        _device.ConnectionStatusChanged += (d, _) =>
            ConnectionChanged?.Invoke(d.ConnectionStatus == BluetoothConnectionStatus.Connected);

        // Mantener viva la conexión GATT (FromBluetoothAddressAsync no conecta solo).
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
    /// Empareja (bonding) el dispositivo si no lo está. CLAVE en Windows: los servicios propietarios
    /// de 128 bits (como el ZAP del Click V2) a menudo NO se enumeran hasta que el dispositivo está
    /// emparejado. El Click V2 usa "Just Works" (sin PIN). Devuelve true si quedó emparejado.
    /// </summary>
    public async Task<bool> EnsurePairedAsync(Action<string>? onDiagnostic = null)
    {
        if (_device == null) throw new InvalidOperationException("Not connected");
        var pairing = _device.DeviceInformation.Pairing;
        if (pairing.IsPaired)
        {
            Console.WriteLine("   🔗 Ya emparejado.");
            return true;
        }

        Console.WriteLine("   🔗 No emparejado. Intentando emparejar (Just Works)…");
        onDiagnostic?.Invoke("Emparejando el mando con Windows (necesario para ver su canal de control)…");

        try
        {
            var custom = pairing.Custom;
            void OnRequested(DeviceInformationCustomPairing s, DevicePairingRequestedEventArgs e)
            {
                // Just Works / confirmar sin PIN.
                e.Accept();
            }
            custom.PairingRequested += OnRequested;
            try
            {
                // ConfirmOnly cubre el "Just Works" de los periféricos Zwift.
                var result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly, DevicePairingProtectionLevel.None);
                Console.WriteLine($"   🔗 Emparejamiento: {result.Status}");
                if (result.Status == DevicePairingResultStatus.Paired ||
                    result.Status == DevicePairingResultStatus.AlreadyPaired)
                    return true;

                // Reintento con el API simple por si el dispositivo prefiere el flujo por defecto.
                var simple = await pairing.PairAsync();
                Console.WriteLine($"   🔗 Emparejamiento (simple): {simple.Status}");
                onDiagnostic?.Invoke($"Resultado del emparejamiento: {simple.Status}.");
                return simple.Status == DevicePairingResultStatus.Paired ||
                       simple.Status == DevicePairingResultStatus.AlreadyPaired;
            }
            finally
            {
                custom.PairingRequested -= OnRequested;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ⚠️ Emparejamiento falló: {ex.Message}");
            onDiagnostic?.Invoke($"No pude emparejar automáticamente ({ex.Message}). Puedes emparejarlo a mano en Configuración → Bluetooth.");
            return false;
        }
    }

    /// <summary>
    /// Resuelve CH02/CH03/CH04 buscándolas por su UUID en TODOS los servicios del dispositivo
    /// (primarios e incluidos), sin caché y con reintentos. Es robusto frente a cómo agrupe Windows
    /// los servicios: lo que importa son las características, estén en el servicio que estén.
    /// </summary>
    public async Task<ZapCharacteristics?> FindZapCharacteristicsAsync(Action<string>? onDiagnostic = null)
    {
        if (_device == null) throw new InvalidOperationException("Not connected");

        const int maxAttempts = 8;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var allChars = new Dictionary<Guid, GattCharacteristic>();
            var allInstances = new List<GattCharacteristic>(); // TODAS las instancias (UUIDs repetidos incluidos)
            var serviceUuids = new List<string>();

            GattDeviceServicesResult primariesResult;
            try
            {
                primariesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠️ Intento {attempt}/{maxAttempts}: GetGattServices: {ex.Message}");
                await Task.Delay(800);
                continue;
            }

            if (primariesResult.Status != GattCommunicationStatus.Success || primariesResult.Services == null)
            {
                Console.WriteLine($"   …intento {attempt}/{maxAttempts}: estado {primariesResult.Status}.");
                if (attempt == 1)
                    onDiagnostic?.Invoke($"El mando aún no responde a la lista de servicios (estado {primariesResult.Status}). Reintentando…");
                await Task.Delay(800);
                continue;
            }

            // Recorrer cada servicio (y sus incluidos) y recolectar TODAS sus características.
            foreach (var svc in primariesResult.Services)
            {
                serviceUuids.Add(Short(svc.Uuid));
                await CollectCharacteristicsAsync(svc, allChars, allInstances);

                try
                {
                    var incl = await svc.GetIncludedServicesAsync(BluetoothCacheMode.Uncached);
                    if (incl.Status == GattCommunicationStatus.Success && incl.Services != null)
                        foreach (var sub in incl.Services)
                            await CollectCharacteristicsAsync(sub, allChars, allInstances);
                }
                catch { /* included services es opcional; ignorar */ }
            }

            allChars.TryGetValue(CH02_UUID, out var ch02);
            allChars.TryGetValue(CH03_UUID, out var ch03);
            allChars.TryGetValue(CH04_UUID, out var ch04);

            if (ch02 != null && ch03 != null)
            {
                var ch02All = allInstances.Where(c => c.Uuid == CH02_UUID).ToList();
                var ch03All = allInstances.Where(c => c.Uuid == CH03_UUID).ToList();
                string extra = ch02All.Count > 1 || ch03All.Count > 1
                    ? $" · {ch02All.Count} CH02 / {ch03All.Count} CH03 (mando puenteado)" : "";
                Console.WriteLine($"   ✅ Características ZAP resueltas (intento {attempt}): CH02+CH03{(ch04 != null ? "+CH04" : "")}{extra}.");
                return new ZapCharacteristics
                {
                    Ch02 = ch02, Ch03 = ch03, Ch04 = ch04,
                    All = new Dictionary<Guid, GattCharacteristic>(allChars),
                    Ch02All = ch02All, Ch03All = ch03All
                };
            }

            string svcList = string.Join(", ", serviceUuids);
            string foundChars = allChars.Count > 0 ? string.Join(", ", allChars.Keys.Select(Short)) : "(ninguna)";
            Console.WriteLine($"   …intento {attempt}/{maxAttempts}: servicios [{svcList}]; chars [{foundChars}]; falta CH02/CH03.");
            if (attempt == 1 || attempt == maxAttempts)
            {
                onDiagnostic?.Invoke($"Servicios vistos: {svcList}.");
                onDiagnostic?.Invoke($"Características vistas: {foundChars}.");
            }

            await Task.Delay(900); // dar tiempo a que la conexión GATT enumere por completo
        }

        return null;
    }

    private static async Task CollectCharacteristicsAsync(GattDeviceService svc,
        Dictionary<Guid, GattCharacteristic> sink, List<GattCharacteristic> allInstances)
    {
        try
        {
            var chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (chars.Status == GattCommunicationStatus.Success && chars.Characteristics != null)
                foreach (var c in chars.Characteristics)
                {
                    sink.TryAdd(c.Uuid, c);   // primera instancia por UUID (para CH04/CH100/101/102)
                    allInstances.Add(c);      // TODAS las instancias, incluso UUIDs repetidos entre servicios
                }
        }
        catch { /* algún servicio puede rechazar la enumeración; ignorar */ }
    }

    /// <summary>Forma corta de un UUID para diagnóstico.</summary>
    private static string Short(Guid g)
    {
        string s = g.ToString();
        if (s.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase))
            return "0x" + s.Substring(4, 4);
        if (s.Contains("19ca-4651-86e5-fa29dcdd09d1", StringComparison.OrdinalIgnoreCase))
            return "ZAP:" + s.Substring(0, 8);
        return s.Substring(0, 8) + "…";
    }

    /// <summary>Desconecta, desempareja opcionalmente y libera el dispositivo.</summary>
    public void Disconnect()
    {
        _session?.Dispose();
        _session = null;
        _device?.Dispose();
        _device = null;
    }
}
