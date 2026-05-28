namespace ZwiftClickV2.Bridge.Crypto;

/// <summary>
/// Factory que detecta la versión del protocolo ZP según los sufijos
/// de la respuesta del handshake y crea la implementación de cifrado adecuada.
/// 
/// V1 (Play 2023): sufijos 01 01, 00 09 → AES-128-CCM
/// V2 (Click 2025): sufijos 02 03, 01 02 → AES-128-GCM, salt 96B
/// </summary>
public static class ZPEncryptionFactory
{
    /// <summary>
    /// Versiones de protocolo detectadas.
    /// </summary>
    public enum ProtocolVersion { Unknown, V1, V2 }

    /// <summary>
    /// Determina la versión del protocolo a partir del sufijo de respuesta del handshake.
    /// </summary>
    /// <param name="responseSuffix">Sufijo de 2 bytes después de "RideOn" (ej: 01 01, 02 03).</param>
    /// <returns>Versión detectada.</returns>
    public static ProtocolVersion DetectVersion(byte[] responseSuffix)
    {
        if (responseSuffix == null || responseSuffix.Length < 2)
            return ProtocolVersion.Unknown;

        return (responseSuffix[0], responseSuffix[1]) switch
        {
            // V1: Zwift Play 2023
            (0x01, 0x01) => ProtocolVersion.V1,
            (0x00, 0x09) => ProtocolVersion.V1,

            // V2: Zwift Click 2025
            (0x02, 0x03) => ProtocolVersion.V2,
            (0x01, 0x02) => ProtocolVersion.V2,

            _ => ProtocolVersion.Unknown
        };
    }

    /// <summary>
    /// Detecta la versión a partir del array completo de respuesta del handshake.
    /// Busca el patrón "RideOn" + sufijo.
    /// </summary>
    public static ProtocolVersion DetectVersionFromResponse(byte[] response)
    {
        if (response == null || response.Length < 8) return ProtocolVersion.Unknown;

        // Buscar "RideOn"
        for (int i = 0; i <= response.Length - 8; i++)
        {
            if (response[i] == 'R' && response[i + 1] == 'i' && response[i + 2] == 'd' &&
                response[i + 3] == 'e' && response[i + 4] == 'O' && response[i + 5] == 'n')
            {
                if (i + 7 < response.Length)
                    return DetectVersion(response.AsSpan(i + 6, 2).ToArray());
            }
        }
        return ProtocolVersion.Unknown;
    }

    /// <summary>
    /// Crea la implementación de ZPEncryption correcta según la versión detectada.
    /// </summary>
    /// <param name="version">Versión del protocolo.</param>
    /// <returns>Un ZPEncryptionV1 o ZPEncryptionV2.</returns>
    public static IZPEncryption Create(ProtocolVersion version)
    {
        return version switch
        {
            ProtocolVersion.V1 => new ZPEncryptionAdapterV1(),
            ProtocolVersion.V2 => new ZPEncryptionAdapterV2(),
            _ => throw new ArgumentException($"Unsupported protocol version: {version}")
        };
    }
}

/// <summary>
/// Interfaz común para ambas versiones de cifrado ZP.
/// </summary>
public interface IZPEncryption
{
    bool IsInitialized { get; }
    void InitializeV1(System.Security.Cryptography.ECDiffieHellman ourKey, byte[] peerPublicKey65);
    void InitializeV2(System.Security.Cryptography.ECDiffieHellman ourKey, byte[] peerPublicKey65, byte[]? saltPublicKey65 = null);
    byte[] Encrypt(byte[] plaintext);
    byte[] Decrypt(byte[] ciphertextWithTag);
    void ResetCounters();
}

/// <summary>
/// Adaptador para ZPEncryptionV1.
/// </summary>
internal class ZPEncryptionAdapterV1 : IZPEncryption
{
    private readonly ZPEncryptionV1 _v1 = new();

    public bool IsInitialized => _v1.IsInitialized;

    public void InitializeV1(System.Security.Cryptography.ECDiffieHellman ourKey, byte[] peerPublicKey65)
        => _v1.Initialize(ourKey, peerPublicKey65);

    public void InitializeV2(System.Security.Cryptography.ECDiffieHellman ourKey, byte[] peerPublicKey65, byte[]? saltPublicKey65 = null)
        => throw new NotSupportedException("V1 does not support V2 initialization");

    public byte[] Encrypt(byte[] plaintext) => _v1.Encrypt(plaintext);
    public byte[] Decrypt(byte[] ciphertextWithTag) => _v1.Decrypt(ciphertextWithTag);
    public void ResetCounters() => _v1.ResetCounters();
}

/// <summary>
/// Adaptador para ZPEncryptionV2.
/// </summary>
internal class ZPEncryptionAdapterV2 : IZPEncryption
{
    private readonly ZPEncryptionV2 _v2 = new();

    public bool IsInitialized => _v2.IsInitialized;

    public void InitializeV1(System.Security.Cryptography.ECDiffieHellman ourKey, byte[] peerPublicKey65)
        => _v2.Initialize(ourKey, peerPublicKey65);

    public void InitializeV2(System.Security.Cryptography.ECDiffieHellman ourKey, byte[] peerPublicKey65, byte[]? saltPublicKey65 = null)
        => _v2.Initialize(ourKey, peerPublicKey65, saltPublicKey65);

    public byte[] Encrypt(byte[] plaintext) => _v2.Encrypt(plaintext);
    public byte[] Decrypt(byte[] ciphertextWithTag) => _v2.Decrypt(ciphertextWithTag);
    public void ResetCounters() => _v2.ResetCounters();
}