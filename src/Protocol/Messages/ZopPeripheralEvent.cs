namespace ZwiftClickV2.Bridge.Protocol.Messages;

/// <summary>
/// Mensaje PeripheralEvent — evento de botón recibido del Click V2.
/// Se recibe cifrado en CH02 (Notify) tras el unlock.
/// Wire opcode: ZapWireOpcode.ZwiftClickNotification (0x38), según el catálogo del decompile.
/// ⚠️ La estructura interna del evento NO está confirmada contra V2 (los nombres de tipo son
/// provisionales, no asumir el formato de Zwift Play). Ver docs/protocol/.
///
/// Posibles tipos de evento:
/// - LeftClick  → botón izquierdo presionado
/// - RightClick → botón derecho presionado
/// - Hold       → botón mantenido presionado
/// </summary>
public class ZopPeripheralEvent
{
    public enum EventType : byte
    {
        Unknown = 0,
        LeftClick = 1,
        RightClick = 2,
        LeftHold = 3,
        RightHold = 4,
        ButtonRelease = 5
    }

    /// <summary>
    /// Tipo de evento de periférico.
    /// </summary>
    public EventType Type { get; set; }

    /// <summary>
    /// Timestamp del evento (millisegundos).
    /// </summary>
    public uint Timestamp { get; set; }

    /// <summary>
    /// Datos crudos del evento descifrado.
    /// </summary>
    public byte[] RawData { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Intenta parsear un payload descifrado como PeripheralEvent.
    /// </summary>
    public static ZopPeripheralEvent Parse(byte[] plaintext)
    {
        var evt = new ZopPeripheralEvent { RawData = plaintext };

        if (plaintext.Length >= 1)
        {
            evt.Type = plaintext[0] switch
            {
                1 => EventType.LeftClick,
                2 => EventType.RightClick,
                3 => EventType.LeftHold,
                4 => EventType.RightHold,
                5 => EventType.ButtonRelease,
                _ => EventType.Unknown
            };
        }
        if (plaintext.Length >= 5)
            evt.Timestamp = BitConverter.ToUInt32(plaintext, 1);

        return evt;
    }

    /// <summary>
    /// Convierte el evento a un código de tecla para MyWoosh.
    /// </summary>
    public byte ToVirtualKey()
    {
        return Type switch
        {
            EventType.LeftClick => 0x25,  // VK_LEFT
            EventType.RightClick => 0x27, // VK_RIGHT
            EventType.LeftHold => 0x25,   // VK_LEFT (repetido)
            EventType.RightHold => 0x27,  // VK_RIGHT (repetido)
            _ => 0
        };
    }

    public override string ToString() => $"[{Type}] ts={Timestamp}";
}