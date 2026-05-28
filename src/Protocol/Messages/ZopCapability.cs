namespace ZwiftClickV2.Bridge.Protocol.Messages;

/// <summary>
/// Mensaje Capability — enviado por el bridge post-handshake
/// para declarar sus capacidades al dispositivo.
/// </summary>
public class ZopCapability
{
    /// <summary>
    /// Payload protobuf serializado de capabilities.
    /// </summary>
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Crea un capability message con payload mínimo (vacío).
    /// </summary>
    public static ZopCapability CreateDefault()
    {
        return new ZopCapability { Payload = Array.Empty<byte>() };
    }
}