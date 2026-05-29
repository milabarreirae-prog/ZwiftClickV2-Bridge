namespace ZwiftClickV2.Bridge.Protocol;

/// <summary>
/// Opcodes de wire del protocolo ZAP confirmados por jat255/constants.py
/// y evidencia empírica de fuzzing.
///
/// Valores confirmados:
///   0x19 → BatteryStatus: confirmado por respuesta empírica 08 00 10 64 18
///           (Protobuf Field 1=0, Field 2=100%, Field 3=0)
///   0x37 → PeripheralEvent: inferido de jat255/constants.py
///   0x07 → tipo del protocolo (propósito no confirmado)
///   0x15 → tipo del protocolo (propósito no confirmado)
/// </summary>
public static class ZapWireOpcode
{
    public const byte Type07        = 0x07;  // Propósito no confirmado (jat255/constants.py)
    public const byte Type15        = 0x15;  // Propósito no confirmado (jat255/constants.py)
    public const byte BatteryStatus = 0x19;  // Confirmado: 08 00 10 64 18 → batería 100%
    public const byte PeripheralEvent = 0x37; // Inferido de jat255/constants.py
}

/// <summary>
/// Representa los tipos de mensajes del protocolo ZOP (Zwift Open Protocol).
/// Estos mensajes se serializan con Protobuf y se transmiten sobre BLE.
///
/// Estructura conocida del protocolo:
/// - Hello:     Mensaje inicial de handshake enviado por el bridge
/// - Welcome:   Respuesta del dispositivo con su clave pública
/// - Event:     Notificación de eventos de periférico (opcode wire: ZapWireOpcode.PeripheralEvent = 0x37)
/// - Command:   Comandos enviados al dispositivo (cambio de modo, etc.)
/// - Status:    Estado del dispositivo (batería) (opcode wire: ZapWireOpcode.BatteryStatus = 0x19)
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