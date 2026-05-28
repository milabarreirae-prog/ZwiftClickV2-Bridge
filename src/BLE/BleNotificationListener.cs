using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ZwiftClickV2.Bridge.BLE;

/// <summary>
/// Se suscribe a notificaciones/indicaciones de múltiples características GATT.
/// Soporta CH02, CH04, CH102 y 00000006.
/// </summary>
public class BleNotificationListener
{
    private readonly Dictionary<Guid, Action<byte[]>> _handlers = new();
    private readonly Dictionary<Guid, GattCharacteristic> _characteristics = new();

    /// <summary>
    /// Registra una característica para recibir notificaciones.
    /// </summary>
    /// <param name="uuid">UUID de la característica.</param>
    /// <param name="characteristic">Objeto GATT.</param>
    /// <param name="handler">Callback que recibe los datos crudos.</param>
    /// <param name="useIndicate">true = Indicate, false = Notify.</param>
    public async Task SubscribeAsync(Guid uuid, GattCharacteristic characteristic,
        Action<byte[]> handler, bool useIndicate = false)
    {
        _handlers[uuid] = handler;
        _characteristics[uuid] = characteristic;

        characteristic.ValueChanged += (s, args) =>
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            byte[] data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);
            handler(data);
        };

        var cccd = useIndicate
            ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
            : GattClientCharacteristicConfigurationDescriptorValue.Notify;

        await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(cccd);
    }

    /// <summary>
    /// Cancela la suscripción de una característica.
    /// </summary>
    public async Task UnsubscribeAsync(Guid uuid)
    {
        if (_characteristics.TryGetValue(uuid, out var ch))
        {
            await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.None);
            _characteristics.Remove(uuid);
            _handlers.Remove(uuid);
        }
    }

    /// <summary>
    /// Verifica si hay una suscripción activa para el UUID dado.
    /// </summary>
    public bool IsSubscribed(Guid uuid) => _characteristics.ContainsKey(uuid);
}