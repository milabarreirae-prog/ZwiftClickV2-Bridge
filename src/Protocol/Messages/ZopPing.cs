namespace ZwiftClickV2.Bridge.Protocol.Messages;

/// <summary>
/// Mensaje Ping — keep-alive periódico para mantener la conexión.
/// Se envía cada ~5 segundos post-handshake.
/// </summary>
public class ZopPing
{
    /// <summary>
    /// Timestamp o contador del ping.
    /// </summary>
    public uint Timestamp { get; set; }

    /// <summary>
    /// Payload mínimo del ping (puede ser vacío).
    /// </summary>
    public static byte[] Payload => Array.Empty<byte>();
}