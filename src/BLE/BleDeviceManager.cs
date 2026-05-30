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
        Console.WriteLine($"   📡 Conectado: {_device.Name} (Paired: {_device.DeviceInformation.Pairing.IsPaired})");
        return _device;
    }

    /// <summary>
    /// Obtiene todos los servicios GATT del dispositivo.
    /// </summary>
    public async Task<GattDeviceService?> GetServiceAsync(Guid serviceUuid)
    {
        if (_device == null) throw new InvalidOperationException("Not connected");
        var result = await _device.GetGattServicesAsync();
        return result.Services?.FirstOrDefault(s => s.Uuid == serviceUuid);
    }

    /// <summary>
    /// Obtiene una característica GATT por UUID.
    /// </summary>
    public async Task<GattCharacteristic?> GetCharacteristicAsync(Guid serviceUuid, Guid characteristicUuid)
    {
        var service = await GetServiceAsync(serviceUuid);
        if (service == null) return null;
        var chars = await service.GetCharacteristicsAsync();
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
        _device?.Dispose();
        _device = null;
    }
}