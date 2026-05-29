namespace ZwiftClickV2.Bridge.Protocol.Messages;

/// <summary>
/// Mensaje Ping — función no confirmada en implementaciones de referencia.
/// Nota: el keep-alive periódico está confirmado como AUSENTE en jat255/app.py y x.c.
/// Esta clase se conserva como referencia pero no debe usarse en bucles de keep-alive.
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