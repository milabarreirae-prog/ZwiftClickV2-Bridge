using Google.Protobuf;

namespace ZwiftClickV2.Bridge.Protocol;

/// <summary>
/// Serializador y deserializador de mensajes ZOP utilizando Google.Protobuf.
/// Cada tipo de mensaje del protocolo tiene su propio esquema .proto que define
/// la estructura binaria de los datos transmitidos por BLE.
/// 
/// El formato de wire es: [longitud: 2 bytes LE] [payload Protobuf]
/// </summary>
public class ZopSerializer
{
    /// <summary>
    /// Serializa un objeto de mensaje Protobuf a un array de bytes.
    /// </summary>
    /// <typeparam name="T">Tipo del mensaje Protobuf (debe implementar IMessage).</typeparam>
    /// <param name="message">Instancia del mensaje a serializar.</param>
    /// <returns>Bytes serializados en formato Protobuf.</returns>
    public byte[] Serialize<T>(T message) where T : IMessage
    {
        // TODO: Implementar serialización con MessageParser o ToByteArray()
        throw new NotImplementedException();
    }

    /// <summary>
    /// Deserializa un array de bytes a un objeto de mensaje Protobuf.
    /// </summary>
    /// <typeparam name="T">Tipo del mensaje Protobuf esperado.</typeparam>
    /// <param name="data">Bytes en formato Protobuf.</param>
    /// <param name="parser">Parser de Protobuf para el tipo T.</param>
    /// <returns>Instancia del mensaje deserializado.</returns>
    public T Deserialize<T>(byte[] data, MessageParser<T> parser) where T : IMessage<T>
    {
        // TODO: Implementar deserialización con MessageParser.ParseFrom()
        throw new NotImplementedException();
    }

    /// <summary>
    /// Decodifica un mensaje recibido por BLE determinando su tipo
    /// y deserializándolo al objeto ZopMessage correspondiente.
    /// </summary>
    /// <param name="rawData">Datos crudos recibidos del BLE (incluye header de longitud).</param>
    /// <returns>Mensaje ZOP decodificado.</returns>
    public ZopMessage Decode(byte[] rawData)
    {
        // TODO: Leer longitud (2 bytes LE), extraer payload, determinar tipo, deserializar
        throw new NotImplementedException();
    }
}