using System.Security.Cryptography;

namespace ZwiftClickV2.Bridge.Crypto;

/// <summary>
/// Implementa el intercambio de claves usando Elliptic Curve Diffie-Hellman (ECDH)
/// sobre la curva P-256 (secp256r1), que es la utilizada por el protocolo ZP.
/// </summary>
public class EcdhKeyExchange
{
    private ECDiffieHellman? _ecdh;

    /// <summary>
    /// Genera un nuevo par de claves efímeras ECDH P-256.
    /// </summary>
    /// <returns>Clave pública en formato sin comprimir (65 bytes: 0x04 || X || Y).</returns>
    public byte[] GenerateKeyPair()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        // Exportar clave pública en formato sin comprimir
        return _ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        // TODO: Adecuar al formato raw esperado por el protocolo ZP
        throw new NotImplementedException();
    }

    /// <summary>
    /// Deriva el secreto compartido usando la clave privada local y la clave pública remota.
    /// </summary>
    /// <param name="remotePublicKey">Clave pública del dispositivo remoto (raw, 65 bytes).</param>
    /// <returns>Secreto compartido derivado.</returns>
    public byte[] DeriveSharedSecret(byte[] remotePublicKey)
    {
        if (_ecdh == null)
            throw new InvalidOperationException("Debe llamar a GenerateKeyPair primero.");

        // TODO: Importar clave pública remota y derivar shared secret
        throw new NotImplementedException();
    }
}