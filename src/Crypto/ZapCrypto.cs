using System.Security.Cryptography;
using System.Text;

namespace ZwiftClickV2.Bridge.Crypto;

/// <summary>
/// Valor del campo <c>info</c> de HKDF para la sesión ZAP V2.
///
/// ⚠️ SIN RESOLVER contra hardware. El decompile de ZwiftApp.exe (FUN_14050db90)
/// NO llama a <c>add1_hkdf_info</c> y la cadena "handshake data" tiene 0 ocurrencias
/// en el binario → la evidencia estática apunta a <see cref="Empty"/>.
/// "handshake data" (<see cref="LegacyHandshakeData"/>) es herencia de Zwift Play V1
/// (jat255). Si el descifrado contra un Click V2 real falla, probar <see cref="Empty"/>
/// ANTES que cualquier otra cosa.
/// </summary>
public enum HkdfInfoMode
{
    /// <summary>Sin info (la evidencia del decompile). Valor por defecto.</summary>
    Empty,

    /// <summary>"handshake data" — heredado de V1, conservado solo para comparación.</summary>
    LegacyHandshakeData
}

/// <summary>
/// Implementación del esquema cripto ZAP V2 observado en Zwift Click 2025.
///
/// Parámetros confirmados contra el decompile (ver docs/protocol/ZAP_STATE_OF_THE_ART.md):
///   • Curva       : ECDH P-256 (secp256r1), shared secret = X cruda (32B)
///   • HKDF        : SHA-256, salt = devicePubKey[64] ‖ localPubKey[64] (128B, device primero)
///   • HKDF output : 36B → AesKey = output[0:32], IvBase = output[32:36]
///   • Cipher      : AES-256-CCM, tag 4B, AAD vacío
///   • Nonce       : IvBase[4] ‖ counter[4] (counter little-endian) = 8B
///   • Wire        : [counter:4B LE][ciphertext][tag:4B]   (el counter lo añade ZopSequencer)
/// </summary>
public sealed class ZapCrypto
{
    /// <summary>Valor contestado heredado de V1; ver <see cref="HkdfInfoMode"/>.</summary>
    public static readonly byte[] LegacyHandshakeDataInfo = Encoding.ASCII.GetBytes("handshake data");

    private readonly byte[] _hkdfInfo;

    public ZapCrypto(HkdfInfoMode infoMode = HkdfInfoMode.Empty)
    {
        InfoMode = infoMode;
        _hkdfInfo = infoMode == HkdfInfoMode.LegacyHandshakeData
            ? LegacyHandshakeDataInfo
            : Array.Empty<byte>();
    }

    public HkdfInfoMode InfoMode { get; }

    public byte[] AesKey { get; private set; } = Array.Empty<byte>();
    public byte[] IvBase { get; private set; } = Array.Empty<byte>();
    public byte[] HkdfSalt { get; private set; } = Array.Empty<byte>();
    public byte[] HkdfInfo => _hkdfInfo.ToArray();
    public bool IsInitialized => AesKey.Length == 32 && IvBase.Length == 4;

    public void DeriveSessionKey(byte[] devicePublicKey64, byte[] localPublicKey64, byte[] sharedSecret)
    {
        if (devicePublicKey64 == null || devicePublicKey64.Length != 64)
            throw new ArgumentException("Device public key must be 64 bytes without 0x04 prefix", nameof(devicePublicKey64));
        if (localPublicKey64 == null || localPublicKey64.Length != 64)
            throw new ArgumentException("Local public key must be 64 bytes without 0x04 prefix", nameof(localPublicKey64));
        if (sharedSecret == null || sharedSecret.Length != 32)
            throw new ArgumentException("Shared secret must be 32 bytes", nameof(sharedSecret));

        // salt = devicePubKey[64] ‖ localPubKey[64] (device primero, sin prefijo 0x04).
        HkdfSalt = [.. devicePublicKey64, .. localPublicKey64];
        byte[] derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 36, HkdfSalt, _hkdfInfo);
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
