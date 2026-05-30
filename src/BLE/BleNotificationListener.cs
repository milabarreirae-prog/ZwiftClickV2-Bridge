using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ZwiftClickV2.Bridge.BLE;

public sealed class BleNotificationContext
{
    public Guid CharacteristicUuid { get; init; }
    public ushort AttributeHandle { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// Se suscribe a notificaciones/indicaciones de múltiples características GATT.
/// Soporta CH02 (notify), CH04 (indicate) y CH100/101/102.
/// </summary>
public class BleNotificationListener
{
    private readonly Dictionary<Guid, Action<byte[]>> _handlers = new();
    private readonly Dictionary<Guid, Action<BleNotificationContext>> _contextHandlers = new();
    private readonly Dictionary<Guid, GattCharacteristic> _characteristics = new();

    /// <summary>
    /// Registra una característica para recibir notificaciones.
    /// </summary>
    /// <param name="uuid">UUID de la característica.</param>
    /// <param name="characteristic">Objeto GATT.</param>
    /// <param name="handler">Callback que recibe los datos crudos.</param>
    /// <param name="useIndicate">true = Indicate, false = Notify.</param>
    public async Task<GattCommunicationStatus> SubscribeAsync(Guid uuid, GattCharacteristic characteristic,
        Action<byte[]> handler, bool useIndicate = false)
    {
        _handlers[uuid] = handler;
        return await SubscribeAsync(uuid, characteristic, context => handler(context.Data), useIndicate);
    }

    /// <summary>
    /// Registra una característica para recibir notificaciones con metadatos
    /// (UUID, ATT handle y payload).
    /// </summary>
    public async Task<GattCommunicationStatus> SubscribeAsync(Guid uuid, GattCharacteristic characteristic,
        Action<BleNotificationContext> handler, bool useIndicate = false)
    {
        _contextHandlers[uuid] = handler;
        _characteristics[uuid] = characteristic;

        characteristic.ValueChanged += (s, args) =>
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            byte[] data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);

            handler(new BleNotificationContext
            {
                CharacteristicUuid = uuid,
                AttributeHandle = characteristic.AttributeHandle,
                Data = data
            });
        };

        // Elegir el modo según lo que la característica realmente soporte (no asumir).
        var props = characteristic.CharacteristicProperties;
        GattClientCharacteristicConfigurationDescriptorValue cccd;
        if (useIndicate && props.HasFlag(GattCharacteristicProperties.Indicate))
            cccd = GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        else if (props.HasFlag(GattCharacteristicProperties.Notify))
            cccd = GattClientCharacteristicConfigurationDescriptorValue.Notify;
        else if (props.HasFlag(GattCharacteristicProperties.Indicate))
            cccd = GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        else
            return GattCommunicationStatus.Unreachable; // no soporta notify/indicate

        try
        {
            return await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(cccd);
        }
        catch
        {
            return GattCommunicationStatus.Unreachable;
        }
    }

    /// <summary>Propiedades GATT de la característica (Notify/Indicate/Write…), para diagnóstico.</summary>
    public static string DescribeProps(GattCharacteristic c) => c.CharacteristicProperties.ToString();

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
            _contextHandlers.Remove(uuid);
        }
    }

    /// <summary>
    /// Verifica si hay una suscripción activa para el UUID dado.
    /// </summary>
    public bool IsSubscribed(Guid uuid) => _characteristics.ContainsKey(uuid);
}