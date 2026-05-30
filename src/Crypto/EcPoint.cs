namespace ZwiftClickV2.Bridge.Crypto;

/// <summary>
/// Utilidades de punto EC secp256r1 (P-256).
/// </summary>
public static class EcPoint
{
    /// <summary>
    /// Comprime una clave pública cruda de 64 bytes (X[32] ‖ Y[32], sin prefijo)
    /// al formato comprimido de 33 bytes (0x02/0x03 ‖ X[32]) que el d-lock-service
    /// espera en el campo 1 del request de autenticación.
    /// El prefijo es 0x03 si Y es impar, 0x02 si es par.
    /// </summary>
    public static byte[] Compress(byte[] raw64)
    {
        if (raw64 == null || raw64.Length != 64)
            throw new ArgumentException("La clave pública cruda debe ser de 64 bytes (X[32]‖Y[32])", nameof(raw64));

        byte[] x = raw64.AsSpan(0, 32).ToArray();
        byte yLsb = raw64[63];               // byte menos significativo de Y
        byte prefix = (byte)((yLsb & 0x01) == 1 ? 0x03 : 0x02);

        byte[] compressed = new byte[33];
        compressed[0] = prefix;
        Array.Copy(x, 0, compressed, 1, 32);
        return compressed;
    }
}
