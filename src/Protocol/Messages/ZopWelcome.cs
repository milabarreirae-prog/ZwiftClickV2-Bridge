namespace ZwiftClickV2.Bridge.Protocol.Messages;

/// <summary>
/// Mensaje Welcome — respuesta del dispositivo al Hello.
/// Contiene la clave pública ECDH del dispositivo + información de versión/capabilities.
/// </summary>
public class ZopWelcome
{
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    public byte[] Suffix { get; set; } = Array.Empty<byte>(); // 2 bytes después de RideOn
    public byte[]? RawData { get; set; } // Datos adicionales post-pubkey

    public static ZopWelcome Parse(byte[] response)
    {
        var welcome = new ZopWelcome { RawData = response };
        if (response.Length >= 8)
            welcome.Suffix = response.AsSpan(6, 2).ToArray();
        var pk = HandshakeParser.ExtractPublicKey(response);
        if (pk != null) welcome.PublicKey = pk;
        return welcome;
    }
}