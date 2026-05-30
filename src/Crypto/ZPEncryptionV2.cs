using System.Security.Cryptography;

namespace ZwiftClickV2.Bridge.Crypto;

/// <summary>
/// Adaptador con estado (contadores tx/rx) sobre <see cref="ZapCrypto"/> para la sesión ZAP V2.
/// </summary>
public class ZPEncryptionV2
{
    private readonly ZapCrypto _crypto;
    private uint _txCounter;
    private uint _rxCounter;

    public ZPEncryptionV2(HkdfInfoMode infoMode = HkdfInfoMode.Empty)
    {
        _crypto = new ZapCrypto(infoMode);
    }

    public bool IsInitialized => _crypto.IsInitialized;
    public byte[] AesKey => _crypto.AesKey.ToArray();
    public byte[] IvBase => _crypto.IvBase.ToArray();
    public byte[] HkdfSalt => _crypto.HkdfSalt.ToArray();

    public void Initialize(ECDiffieHellman ourKey, byte[] peerPublicKey65, byte[]? saltPublicKey65 = null)
    {
        byte[] localPublicKeyRaw = ExportPublicKeyRaw(ourKey);
        Initialize(ourKey, peerPublicKey65, saltPublicKey65 ?? peerPublicKey65, localPublicKeyRaw);
    }

    public void Initialize(ECDiffieHellman ourKey, byte[] peerPublicKey65, byte[] devicePublicKey, byte[] localPublicKey)
    {
        byte[] peerPublicKeyRaw = NormalizePublicKey(peerPublicKey65, nameof(peerPublicKey65));

        using var peerEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        peerEcdh.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = peerPublicKeyRaw.AsSpan(0, 32).ToArray(),
                Y = peerPublicKeyRaw.AsSpan(32, 32).ToArray()
            }
        });

        // Secreto compartido = coordenada X cruda (estilo OpenSSL ECDH_compute_key),
        // NO SHA256(X). Confirmado por el decompile; ver docs/protocol/ZAP_STATE_OF_THE_ART.md.
        byte[] sharedSecret = ourKey.DeriveRawSecretAgreement(peerEcdh.PublicKey);
        if (sharedSecret.Length != 32)
            throw new CryptographicException($"Shared secret expected 32B, got {sharedSecret.Length}");

        byte[] devicePublicKeyRaw = NormalizePublicKey(devicePublicKey, nameof(devicePublicKey));
        byte[] localPublicKeyRaw = NormalizePublicKey(localPublicKey, nameof(localPublicKey));
        _crypto.DeriveSessionKey(devicePublicKeyRaw, localPublicKeyRaw, sharedSecret);
        _txCounter = 0;
        _rxCounter = 0;
    }

    public byte[] Encrypt(byte[] plaintext)
        => Encrypt(plaintext, null, null);

    public byte[] Encrypt(byte[] plaintext, uint? counter, byte[]? associatedData)
    {
        if (!IsInitialized)
            throw new InvalidOperationException("Not initialized");

        uint effectiveCounter = counter ?? _txCounter++;
        return _crypto.Encrypt(plaintext, effectiveCounter, associatedData);
    }

    public byte[] Decrypt(byte[] ciphertextWithTag)
        => Decrypt(ciphertextWithTag, null, null);

    public byte[] Decrypt(byte[] ciphertextWithTag, uint? counter, byte[]? associatedData)
    {
        if (!IsInitialized)
            throw new InvalidOperationException("Not initialized");
        if (ciphertextWithTag.Length < 4)
            throw new ArgumentException("Message too short");

        uint effectiveCounter = counter ?? _rxCounter++;
        try
        {
            return _crypto.Decrypt(ciphertextWithTag, effectiveCounter, associatedData);
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException($"CCM tag mismatch (rx={effectiveCounter})", ex);
        }
    }

    public byte[] BuildNonceForCounter(uint counter)
        => _crypto.BuildNonce(counter);

    public byte[] GetHkdfInfo()
        => _crypto.HkdfInfo;

    public void ResetCounters() { _txCounter = 0; _rxCounter = 0; }

    private static byte[] NormalizePublicKey(byte[] publicKey, string paramName)
    {
        if (publicKey == null)
            throw new ArgumentNullException(paramName);

        if (publicKey.Length == 65 && publicKey[0] == 0x04)
            return publicKey.AsSpan(1, 64).ToArray();

        if (publicKey.Length == 64)
            return publicKey.ToArray();

        throw new ArgumentException("La clave pública debe ser de 64 bytes raw o 65 bytes con prefijo 0x04", paramName);
    }

    private static byte[] ExportPublicKeyRaw(ECDiffieHellman key)
    {
        var parameters = key.ExportParameters(false);
        return [.. parameters.Q.X!, .. parameters.Q.Y!];
    }
}
