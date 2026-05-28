namespace ZwiftClickV2.Bridge.Protocol;

/// <summary>
/// Representa los tipos de mensajes del protocolo ZOP (Zwift Open Protocol).
/// Estos mensajes se serializan con Protobuf y se transmiten sobre BLE.
/// 
/// Estructura conocida del protocolo:
/// - Hello:     Mensaje inicial de handshake enviado por el bridge
/// - Welcome:   Respuesta del dispositivo con su clave pública
/// - Event:     Notificación de eventos (click, presión, etc.)
/// - Command:   Comandos enviados al dispositivo (cambio de modo, etc.)
/// - Status:    Estado del dispositivo (batería, versión, etc.)
/// </summary>
public enum ZopMessageType
{
    Unknown = 0,
    Hello = 1,
    Welcome = 2,
    Event = 3,
    Command = 4,
    Status = 5
}

/// <summary>
/// Mensaje base del protocolo ZOP. Contiene el tipo de mensaje y los datos crudos.
/// La estructura interna de los datos depende del tipo de mensaje y se deserializa
/// con el esquema Protobuf correspondiente.
/// </summary>
public class ZopMessage
{
    /// <summary>
    /// Tipo de mensaje ZOP.
    /// </summary>
    public ZopMessageType MessageType { get; set; }

    /// <summary>
    /// Datos crudos del mensaje (payload Protobuf).
    /// </summary>
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Timestamp de recepción/envío del mensaje.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Crea un mensaje ZOP con el tipo especificado.
    /// </summary>
    /// <param name="type">Tipo de mensaje.</param>
    /// <param name="payload">Datos serializados del mensaje.</param>
    public static ZopMessage Create(ZopMessageType type, byte[] payload)
    {
        return new ZopMessage
        {
            MessageType = type,
            Payload = payload,
            Timestamp = DateTimeOffset.UtcNow
        };
    }
}