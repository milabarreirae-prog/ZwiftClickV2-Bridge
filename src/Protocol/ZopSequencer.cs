using ZwiftClickV2.Bridge.Crypto;

namespace ZwiftClickV2.Bridge.Protocol;

/// <summary>
/// Maneja la secuenciación y cifrado/descifrado de mensajes ZOP.
/// Cada mensaje se envía con un sequence number uint32 little-endian
/// seguido del payload (posiblemente cifrado).
/// 
/// Formato de wire: [seq:4B LE] + [ciphered_payload]
/// </summary>
public class ZopSequencer
{
    private readonly IZPEncryption _crypto;
    private uint _txSequence;
    private uint _rxSequence;

    public ZopSequencer(IZPEncryption crypto)
    {
        _crypto = crypto;
        _txSequence = 0;
        _rxSequence = 0;
    }

    /// <summary>
    /// Cifra un payload y lo empaqueta con sequence number.
    /// </summary>
    public byte[] PackageAndEncrypt(byte[] plaintext)
    {
        byte[] ciphertext = _crypto.Encrypt(plaintext);
        return Package(ciphertext, _txSequence++);
    }

    /// <summary>
    /// Desempaqueta un frame, extrae el sequence number y descifra el payload.
    /// Retorna (sequence, plaintext).
    /// </summary>
    public (uint Sequence, byte[] Plaintext) UnpackAndDecrypt(byte[] frame)
    {
        if (frame.Length < 4)
            throw new ArgumentException("Frame too short");

        uint seq = BitConverter.ToUInt32(frame, 0); // LE
        byte[] ciphertext = frame.AsSpan(4).ToArray();
        byte[] plaintext = _crypto.Decrypt(ciphertext);
        _rxSequence = seq + 1; // Siguiente esperado
        return (seq, plaintext);
    }

    /// <summary>
    /// Empaqueta un payload (ya cifrado) con sequence number.
    /// </summary>
    public static byte[] Package(byte[] payload, uint sequence)
    {
        byte[] frame = new byte[4 + payload.Length];
        frame[0] = (byte)(sequence);
        frame[1] = (byte)(sequence >> 8);
        frame[2] = (byte)(sequence >> 16);
        frame[3] = (byte)(sequence >> 24);
        Array.Copy(payload, 0, frame, 4, payload.Length);
        return frame;
    }

    /// <summary>
    /// Desempaqueta un frame extrayendo el sequence number y el payload.
    /// </summary>
    public static (uint Sequence, byte[] Payload) Unpack(byte[] frame)
    {
        if (frame.Length < 4)
            throw new ArgumentException("Frame too short");
        uint seq = BitConverter.ToUInt32(frame, 0);
        return (seq, frame.AsSpan(4).ToArray());
    }

    public void Reset() { _txSequence = 0; _rxSequence = 0; }
}