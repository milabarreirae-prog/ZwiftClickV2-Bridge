namespace ZwiftClickV2.Bridge.Protocol.Messages;

/// <summary>
/// Mensaje Hello — enviado por el bridge al dispositivo para iniciar el handshake.
/// Contiene la clave pública ECDH P-256 del bridge (65 bytes uncompressed).
/// 
/// Formato wire: "RideOn" + sufijo + pubkey[65] (durante el handshake inicial)
/// Formato ZOP: [seq LE 4B] + [ciphered protobuf] (post-handshake)
/// </summary>
public class ZopHello
{
    /// <summary>
    /// Clave pública ECDH P-256 en formato uncompressed (65 bytes: 0x04 + X + Y).
    /// </summary>
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Sufijo de handshake (por defecto 0x01 0x02 para V2).
    /// </summary>
    public byte[] Suffix { get; set; } = new byte[] { 0x01, 0x02 };

    /// <summary>
    /// Construye el payload crudo para el handshake inicial: "RideOn" + sufijo + pubkey.
    /// </summary>
    public byte[] BuildHandshakePayload()
    {
        byte[] rideOn = "RideOn"u8.ToArray();
        byte[] result = new byte[rideOn.Length + Suffix.Length + PublicKey.Length];
        Array.Copy(rideOn, 0, result, 0, rideOn.Length);
        Array.Copy(Suffix, 0, result, rideOn.Length, Suffix.Length);
        Array.Copy(PublicKey, 0, result, rideOn.Length + Suffix.Length, PublicKey.Length);
        return result;
    }
}