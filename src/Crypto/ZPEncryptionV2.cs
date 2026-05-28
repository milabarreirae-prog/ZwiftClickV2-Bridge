using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace ZwiftClickV2.Bridge.Crypto;

/// <summary>
/// ZPEncryption V2 — Click 2025 (AES-128-GCM, tag 4B, salt 96B).
/// 
/// ECDH P-256 → shared secret (32B)
/// Salt = peer_pub[1:65] (64B) || SHA256(peer_pub[1:65]) (32B) = 96B
/// HKDF-Extract(SHA256, salt, IKM=sharedSecret) → PRK
/// HKDF-Expand(SHA256, PRK, info=NULL, L=36) → 36B
///   derived[0:16]  = AES key
///   derived[32:36] = nonce (4B)
/// AES-128-GCM: IV = nonce[4] || counter[4] BE = 8B, tag = 4B
/// </summary>
public class ZPEncryptionV2
{
    private byte[]? _aesKey;
    private byte[]? _nonce;
    private uint _txCounter;
    private uint _rxCounter;

    public bool IsInitialized => _aesKey != null;

    public void Initialize(ECDiffieHellman ourKey, byte[] peerPublicKey65, byte[]? saltPublicKey65 = null)
    {
        if (peerPublicKey65 == null || peerPublicKey65.Length != 65 || peerPublicKey65[0] != 0x04)
            throw new ArgumentException("La clave pública del peer debe ser de 65 bytes con prefijo 0x04");

        using var peerEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        peerEcdh.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = peerPublicKey65.AsSpan(1, 32).ToArray(),
                Y = peerPublicKey65.AsSpan(33, 32).ToArray()
            }
        });

        byte[] sharedSecret = ourKey.DeriveKeyMaterial(peerEcdh.PublicKey);
        if (sharedSecret.Length != 32)
            throw new CryptographicException($"Shared secret expected 32B, got {sharedSecret.Length}");

        byte[] saltPubKey = saltPublicKey65 ?? peerPublicKey65;
        if (saltPubKey.Length != 65 || saltPubKey[0] != 0x04)
            throw new ArgumentException("Salt pubkey must be 65B with 0x04 prefix");

        byte[] saltPubRaw = saltPubKey.AsSpan(1, 64).ToArray();
        byte[] saltPubHash = SHA256.HashData(saltPubRaw);
        byte[] salt = [.. saltPubRaw, .. saltPubHash]; // 96B

        byte[] derivedKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 36, salt, info: null);

        _aesKey = derivedKey.AsSpan(0, 16).ToArray();
        _nonce = derivedKey.AsSpan(32, 4).ToArray();
        _txCounter = 0;
        _rxCounter = 0;
    }

    public byte[] Encrypt(byte[] plaintext)
    {
        if (_aesKey == null || _nonce == null)
            throw new InvalidOperationException("Not initialized");

        byte[] iv = BuildIV(_txCounter++);

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(new KeyParameter(_aesKey), 32, iv, null));

        byte[] output = new byte[cipher.GetOutputSize(plaintext.Length)];
        int len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        cipher.DoFinal(output, len);
        return output;
    }

    public byte[] Decrypt(byte[] ciphertextWithTag)
    {
        if (_aesKey == null || _nonce == null)
            throw new InvalidOperationException("Not initialized");
        if (ciphertextWithTag.Length < 4)
            throw new ArgumentException("Message too short");

        byte[] iv = BuildIV(_rxCounter++);

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(false, new AeadParameters(new KeyParameter(_aesKey), 32, iv, null));

        byte[] output = new byte[cipher.GetOutputSize(ciphertextWithTag.Length)];
        int len = cipher.ProcessBytes(ciphertextWithTag, 0, ciphertextWithTag.Length, output, 0);

        try { cipher.DoFinal(output, len); }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException ex)
        { throw new CryptographicException($"GCM tag mismatch (rx={_rxCounter - 1})", ex); }

        return output;
    }

    private byte[] BuildIV(uint counter)
    {
        byte[] iv = new byte[8];
        Array.Copy(_nonce!, 0, iv, 0, 4);
        iv[4] = (byte)(counter >> 24);
        iv[5] = (byte)(counter >> 16);
        iv[6] = (byte)(counter >> 8);
        iv[7] = (byte)(counter);
        return iv;
    }

    public void ResetCounters() { _txCounter = 0; _rxCounter = 0; }
}