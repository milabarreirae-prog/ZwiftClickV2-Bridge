using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ZwiftClickV2.Bridge.BLE;

/// <summary>
/// Escribe datos en características GATT con formato ZOP: [seq:4B LE] + [payload].
/// </summary>
public class BleCharacteristicWriter
{
    private readonly Dictionary<Guid, GattCharacteristic> _characteristics = new();

    /// <summary>
    /// Registra una característica para escritura.
    /// </summary>
    public void RegisterCharacteristic(Guid uuid, GattCharacteristic characteristic)
    {
        _characteristics[uuid] = characteristic;
    }

    /// <summary>
    /// Escribe un payload crudo en una característica (sin sequence number).
    /// Útil para el handshake inicial.
    /// </summary>
    public async Task<GattCommunicationStatus> WriteRawAsync(Guid uuid, byte[] payload, bool withResponse = false)
    {
        if (!_characteristics.TryGetValue(uuid, out var ch))
            throw new InvalidOperationException($"Characteristic {uuid} not registered");

        var writer = new DataWriter();
        writer.WriteBytes(payload);
        var option = withResponse ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        return await ch.WriteValueAsync(writer.DetachBuffer(), option);
    }

    /// <summary>
    /// Escribe un payload con formato ZOP: [sequence:4B LE] + [payload].
    /// </summary>
    /// <param name="uuid">UUID de la característica.</param>
    /// <param name="sequence">Número de secuencia (uint32).</param>
    /// <param name="payload">Payload a escribir (sin cifrar o ya cifrado).</param>
    /// <param name="withResponse">Si requiere response del dispositivo.</param>
    public async Task<GattCommunicationStatus> WriteWithSequenceAsync(
        Guid uuid, uint sequence, byte[] payload, bool withResponse = false)
    {
        if (!_characteristics.TryGetValue(uuid, out var ch))
            throw new InvalidOperationException($"Characteristic {uuid} not registered");

        // Construir: [seq LE 4B] + [payload]
        byte[] frame = new byte[4 + payload.Length];
        frame[0] = (byte)(sequence);
        frame[1] = (byte)(sequence >> 8);
        frame[2] = (byte)(sequence >> 16);
        frame[3] = (byte)(sequence >> 24);
        Array.Copy(payload, 0, frame, 4, payload.Length);

        var writer = new DataWriter();
        writer.WriteBytes(frame);
        var option = withResponse ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        return await ch.WriteValueAsync(writer.DetachBuffer(), option);
    }

    /// <summary>
    /// Verifica si una característica está registrada.
    /// </summary>
    public bool IsRegistered(Guid uuid) => _characteristics.ContainsKey(uuid);
}