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

    // UUIDs Zwift
    public static readonly Guid ZWIFT_SERVICE_UUID = Guid.Parse("0000fc82-0000-1000-8000-00805f9b34fb");
    public static readonly Guid CH02_UUID = Guid.Parse("00000002-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH03_UUID = Guid.Parse("00000003-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH04_UUID = Guid.Parse("00000004-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH06_UUID = Guid.Parse("00000006-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH100_UUID = Guid.Parse("00000100-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH101_UUID = Guid.Parse("00000101-19ca-4651-86e5-fa29dcdd09d1");
    public static readonly Guid CH102_UUID = Guid.Parse("00000102-19ca-4651-86e5-fa29dcdd09d1");

    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    /// <summary>
    /// Escanea dispositivos BLE buscando "Zwift Click" y se conecta.
    /// </summary>
    public async Task<BluetoothLEDevice?> ConnectAsync(string deviceName = "Zwift Click", TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(30);
        Console.WriteLine($"🔍 Escaneando '{deviceName}' ({timeout.Value.TotalSeconds:F0}s)...");

        var tcs = new TaskCompletionSource<ulong>();
        using var cts = new CancellationTokenSource(timeout.Value);

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (s, args) =>
        {
            if (!string.IsNullOrEmpty(args.Advertisement.LocalName) &&
                args.Advertisement.LocalName.StartsWith(deviceName, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"   ✅ Encontrado: {args.Advertisement.LocalName} (0x{args.BluetoothAddress:X})");
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
    /// Lee la característica 00000006 (datos de estado/info del dispositivo).
    /// </summary>
    public async Task<byte[]?> ReadCharacteristic06Async()
    {
        return await ReadCharacteristicAsync(ZWIFT_SERVICE_UUID, CH06_UUID);
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