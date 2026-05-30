namespace ZwiftClickV2.Bridge.Protocol;

/// <summary>
/// Opcodes de wire del protocolo ZAP, según la tabla autoritativa del decompile de
/// ZwiftApp.exe (FUN_140500800, x.c:814440-814690). Ver docs/protocol/opcode-catalog.md.
///
/// ⚠️ Los valores V1/jat255 estaban MAL mapeados para el Click V2. Correcciones clave:
///   0x15 = CONTROLLER_REQUEST (no "empty/keep-alive")
///   0x19 = RESET             (no BATTERY_LEVEL)
///   0x23 = BATTERY_STATUS    (el verdadero; el payload 08 00 10 64 18 va precedido de 0x23)
///   0x37 = ZWIFT_PLAY_DEVICE_STATUS (no CLICK_NOTIFICATION)
///   0x38 = ZWIFT_CLICK_NOTIFICATION (el verdadero opcode de botones del Click V2)
/// No existe opcode KEEP_ALIVE/PING en el catálogo (Ping/Pong son del servidor ZOP, no ZAP).
/// </summary>
public static class ZapWireOpcode
{
    public const byte TrainerConfigStatus     = 0x07;
    public const byte ZwiftPlayNotif          = 0x08;
    public const byte ControllerRequest       = 0x15;
    public const byte Reset                   = 0x19;
    public const byte BatteryStatus           = 0x23;
    public const byte ControllerNotification  = 0x28;
    public const byte ZwiftPlayDeviceStatus   = 0x37;
    public const byte ZwiftClickNotification  = 0x38;
    public const byte LostControl             = 0xFF;
}

/// <summary>
/// Representa los tipos de mensajes del protocolo ZOP (Zwift Open Protocol).
/// Estos mensajes se serializan con Protobuf y se transmiten sobre BLE.
///
/// Estructura conocida del protocolo:
/// - Hello:     Mensaje inicial de handshake enviado por el bridge
/// - Welcome:   Respuesta del dispositivo con su clave pública
/// - Event:     Notificación de botones del Click (opcode wire: ZapWireOpcode.ZwiftClickNotification = 0x38)
/// - Command:   Comandos enviados al dispositivo (cambio de modo, etc.)
/// - Status:    Estado del dispositivo (batería) (opcode wire: ZapWireOpcode.BatteryStatus = 0x23)
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