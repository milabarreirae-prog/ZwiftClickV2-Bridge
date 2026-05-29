using System.Security.Cryptography;
using System.Text;

namespace ZwiftClickV2.Bridge.Crypto;

/// <summary>
/// Implementación del esquema cripto ZAP V2 observado en Zwift Click 2025.
/// </summary>
public sealed class ZapCrypto
{
    private static readonly byte[] HkdfInfoBytes = Encoding.ASCII.GetBytes("handshake data");

    public byte[] AesKey { get; private set; } = Array.Empty<byte>();
    public byte[] IvBase { get; private set; } = Array.Empty<byte>();
    public byte[] HkdfSalt { get; private set; } = Array.Empty<byte>();
    public byte[] HkdfInfo => HkdfInfoBytes.ToArray();
    public bool IsInitialized => AesKey.Length == 32 && IvBase.Length == 4;

    public void DeriveSessionKey(byte[] devicePublicKey64, byte[] localPublicKey64, byte[] sharedSecret)
    {
        if (devicePublicKey64 == null || devicePublicKey64.Length != 64)
            throw new ArgumentException("Device public key must be 64 bytes without 0x04 prefix", nameof(devicePublicKey64));
        if (localPublicKey64 == null || localPublicKey64.Length != 64)
            throw new ArgumentException("Local public key must be 64 bytes without 0x04 prefix", nameof(localPublicKey64));
        if (sharedSecret == null || sharedSecret.Length != 32)
            throw new ArgumentException("Shared secret must be 32 bytes", nameof(sharedSecret));

        HkdfSalt = [.. devicePublicKey64, .. localPublicKey64];
        byte[] derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 36, HkdfSalt, HkdfInfoBytes);
        AesKey = derived.AsSpan(0, 32).ToArray();
        IvBase = derived.AsSpan(32, 4).ToArray();
    }

    public byte[] Encrypt(byte[] plaintext, uint counter, byte[]? associatedData = null)
    {
        EnsureInitialized();

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[4];

        using var aesCcm = new AesCcm(AesKey);
        aesCcm.Encrypt(BuildNonce(counter), plaintext, ciphertext, tag, associatedData: associatedData);
        return [.. ciphertext, .. tag];
    }

    public byte[] Decrypt(byte[] ciphertextWithTag, uint counter, byte[]? associatedData = null)
    {
        EnsureInitialized();
        if (ciphertextWithTag == null || ciphertextWithTag.Length < 4)
            throw new ArgumentException("Ciphertext must include a 4-byte tag", nameof(ciphertextWithTag));

        int ciphertextLength = ciphertextWithTag.Length - 4;
        byte[] ciphertext = ciphertextWithTag.AsSpan(0, ciphertextLength).ToArray();
        byte[] tag = ciphertextWithTag.AsSpan(ciphertextLength, 4).ToArray();
        byte[] plaintext = new byte[ciphertextLength];

        using var aesCcm = new AesCcm(AesKey);
        aesCcm.Decrypt(BuildNonce(counter), ciphertext, tag, plaintext, associatedData: associatedData);
        return plaintext;
    }

    public byte[] BuildNonce(uint counter)
    {
        EnsureInitialized();

        // Nonce V2: IV base (4B) || counter little-endian (4B).
        return new byte[]
        {
            IvBase[0], IvBase[1], IvBase[2], IvBase[3],
            (byte)counter,
            (byte)(counter >> 8),
            (byte)(counter >> 16),
            (byte)(counter >> 24)
        };
    }

    private void EnsureInitialized()
    {
        if (!IsInitialized)
            throw new InvalidOperationException("ZapCrypto is not initialized");
    }
}