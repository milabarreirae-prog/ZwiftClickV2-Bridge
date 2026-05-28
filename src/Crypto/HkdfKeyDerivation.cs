using System.Security.Cryptography;

namespace ZwiftClickV2.Bridge.Crypto;

/// <summary>
/// Implementa la derivación de claves usando HKDF (HMAC-based Key Derivation Function)
/// según RFC 5869. Se utiliza para derivar la clave de sesión AES-128 a partir del
/// secreto compartido de ECDH.
/// </summary>
public class HkdfKeyDerivation
{
    /// <summary>
    /// Deriva una clave usando HKDF-SHA256.
    /// </summary>
    /// <param name="inputKeyMaterial">Material de clave de entrada (shared secret de ECDH).</param>
    /// <param name="salt">Salt opcional (puede ser null o array vacío).</param>
    /// <param name="info">Información de contexto (datos específicos del protocolo).</param>
    /// <param name="outputLength">Longitud de la clave de salida en bytes (16 para AES-128).</param>
    /// <returns>Clave derivada del largo especificado.</returns>
    public byte[] DeriveKey(byte[] inputKeyMaterial, byte[]? salt, byte[] info, int outputLength)
    {
        // TODO: Implementar HKDF-SHA256 con HMACSHA256
        // HKDF-Extract: PRK = HMAC-SHA256(salt, IKM)
        // HKDF-Expand: OKM = HMAC-SHA256(PRK, info || counter)
        throw new NotImplementedException();
    }

    /// <summary>
    /// Deriva una clave de sesión AES-128 (16 bytes) con defaults seguros.
    /// </summary>
    /// <param name="sharedSecret">Secreto compartido de ECDH.</param>
    /// <param name="contextInfo">Información de contexto del protocolo.</param>
    /// <returns>Clave AES-128 de 16 bytes.</returns>
    public byte[] DeriveAes128Key(byte[] sharedSecret, byte[] contextInfo)
    {
        byte[] salt = new byte[32]; // Salt de ceros (32 bytes para SHA-256)
        return DeriveKey(sharedSecret, salt, contextInfo, outputLength: 16);
    }
}