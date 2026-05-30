using System.Numerics;

namespace ZwiftClickV2.Bridge.Crypto;

/// <summary>
/// Utilidades de punto EC secp256r1 (P-256): compresión y descompresión.
/// </summary>
public static class EcPoint
{
    // Parámetros de la curva P-256 (secp256r1).
    private static readonly BigInteger P = BigInteger.Parse(
        "0FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger A = P - 3; // a = -3 mod p
    private static readonly BigInteger B = BigInteger.Parse(
        "05AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B", System.Globalization.NumberStyles.HexNumber);

    /// <summary>
    /// Comprime una clave pública cruda de 64 bytes (X[32] ‖ Y[32], sin prefijo) al formato
    /// comprimido de 33 bytes (0x02/0x03 ‖ X[32]). El prefijo es 0x03 si Y es impar, 0x02 si par.
    /// </summary>
    public static byte[] Compress(byte[] raw64)
    {
        if (raw64 == null || raw64.Length != 64)
            throw new ArgumentException("La clave pública cruda debe ser de 64 bytes (X[32]‖Y[32])", nameof(raw64));

        byte[] compressed = new byte[33];
        compressed[0] = (byte)(0x02 | (raw64[63] & 1)); // paridad del último byte de Y
        Array.Copy(raw64, 0, compressed, 1, 32);
        return compressed;
    }

    /// <summary>
    /// Descomprime un punto comprimido de 33 bytes (0x02/0x03 ‖ X) a la forma cruda de 64 bytes
    /// (X[32] ‖ Y[32]), recuperando Y con la ecuación de la curva: y² = x³ - 3x + b (mod p), y
    /// y = (y²)^((p+1)/4) ajustando la paridad al prefijo. Útil para derivar la clave de sesión
    /// post-unlock a partir del campo 1 del reto del dispositivo.
    /// </summary>
    public static byte[] Decompress(byte[] compressed33)
    {
        if (compressed33 == null || compressed33.Length != 33 ||
            (compressed33[0] != 0x02 && compressed33[0] != 0x03))
            throw new ArgumentException("Punto comprimido inválido (33B con prefijo 0x02/0x03)", nameof(compressed33));

        int wantOdd = compressed33[0] & 1;
        var x = new BigInteger(compressed33.AsSpan(1, 32).ToArray(), isUnsigned: true, isBigEndian: true);

        BigInteger rhs = (BigInteger.ModPow(x, 3, P) + A * x + B) % P;
        if (rhs < 0) rhs += P;

        BigInteger y = BigInteger.ModPow(rhs, (P + 1) / 4, P); // p ≡ 3 (mod 4) → raíz cuadrada directa
        if (y * y % P != rhs)
            throw new ArgumentException("El punto comprimido no está en la curva P-256", nameof(compressed33));

        if (((int)(y & 1)) != wantOdd)
            y = P - y;

        byte[] raw64 = new byte[64];
        Array.Copy(compressed33, 1, raw64, 0, 32); // X
        byte[] yb = y.ToByteArray(isUnsigned: true, isBigEndian: true);
        Array.Copy(yb, 0, raw64, 64 - yb.Length, yb.Length); // Y alineado a la derecha (32B)
        return raw64;
    }
}
