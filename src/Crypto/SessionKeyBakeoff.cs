using System.Security.Cryptography;
using System.Text;

namespace ZwiftClickV2.Bridge.Crypto;

/// <summary>
/// Resuelve los dos parámetros de interop que quedan sin confirmar para la sesión post-unlock,
/// usando la **validez del tag AES-CCM como oráculo** (clave equivocada → tag inválido → descarte):
///
///   secret ∈ { X cruda (DeriveRawSecretAgreement), SHA256(secret) (DeriveKeyMaterial) }
///   info   ∈ { vacío, "handshake data" (V1/jat255) }
///
/// → 4 candidatos (key, ivBase). El que descifra limpiamente una notificación real de CH02
/// (`[4B counter][ct][4B tag]`) es la verdad. Autovalidante: no necesita conocer el plaintext
/// esperado, solo que el tag CCM valide. Equivale al `--hkdf-bakeoff` del probe de investigación.
/// </summary>
public static class SessionKeyBakeoff
{
    public sealed class Candidate
    {
        public required string Label { get; init; }
        public required byte[] Key { get; init; }     // 32B
        public required byte[] IvBase { get; init; }  // 4B

        /// <summary>Intenta descifrar un paquete <c>[4B counter][ct][4B tag]</c>. true si el tag valida.</summary>
        public bool TryDecrypt(byte[] packet, out byte[] plaintext)
        {
            plaintext = Array.Empty<byte>();
            if (packet.Length < 4 + 4) return false;
            try
            {
                byte[] counter = packet.AsSpan(0, 4).ToArray();
                byte[] ciphertext = packet.AsSpan(4, packet.Length - 8).ToArray();
                byte[] tag = packet.AsSpan(packet.Length - 4, 4).ToArray();

                byte[] nonce = new byte[8];
                Array.Copy(IvBase, 0, nonce, 0, 4);
                Array.Copy(counter, 0, nonce, 4, 4);

                byte[] pt = new byte[ciphertext.Length];
                using var ccm = new AesCcm(Key);
                ccm.Decrypt(nonce, ciphertext, tag, pt, associatedData: Array.Empty<byte>());
                plaintext = pt;
                return true;
            }
            catch (CryptographicException) { return false; } // tag mismatch = candidato equivocado
            catch (ArgumentException) { return false; }
        }
    }

    /// <summary>
    /// Construye los 4 candidatos a partir de un mismo par EC local y la pubkey cruda del dispositivo.
    /// salt = devicePub[64] ‖ localPub[64] (128B), output 36B → key[0:32] + ivBase[32:36].
    /// </summary>
    public static IReadOnlyList<Candidate> BuildCandidates(ECDiffieHellman ourKey, byte[] devicePub64, byte[] localPub64)
    {
        if (devicePub64 is not { Length: 64 }) throw new ArgumentException("devicePub64 debe ser 64B", nameof(devicePub64));
        if (localPub64 is not { Length: 64 }) throw new ArgumentException("localPub64 debe ser 64B", nameof(localPub64));

        using var deviceEcdh = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = devicePub64[..32], Y = devicePub64[32..64] }
        });
        var devicePubKey = deviceEcdh.PublicKey;

        var secrets = new (string Name, byte[] Bytes)[]
        {
            ("raw", ourKey.DeriveRawSecretAgreement(devicePubKey)),     // X cruda (OpenSSL-style)
            ("hashed", ourKey.DeriveKeyMaterial(devicePubKey)),         // SHA256(secret) (.NET default)
        };
        var infos = new (string Name, byte[] Bytes)[]
        {
            ("empty", Array.Empty<byte>()),
            ("handshake data", Encoding.ASCII.GetBytes("handshake data")),
        };

        byte[] salt = [.. devicePub64, .. localPub64];

        var candidates = new List<Candidate>(4);
        foreach (var s in secrets)
            foreach (var i in infos)
            {
                byte[] outp = HKDF.DeriveKey(HashAlgorithmName.SHA256, s.Bytes, 36, salt, i.Bytes);
                candidates.Add(new Candidate
                {
                    Label = $"secret={s.Name}, info={i.Name}",
                    Key = outp.AsSpan(0, 32).ToArray(),
                    IvBase = outp.AsSpan(32, 4).ToArray(),
                });
            }
        return candidates;
    }

    /// <summary>
    /// Devuelve el primer candidato que descifra limpiamente alguno de los paquetes (tag válido), o
    /// null si ninguno lo logra. <paramref name="plaintext"/> es el plaintext de la primera coincidencia.
    /// </summary>
    public static Candidate? Resolve(IEnumerable<Candidate> candidates, IEnumerable<byte[]> packets, out byte[] plaintext)
    {
        plaintext = Array.Empty<byte>();
        foreach (byte[] pkt in packets)
            foreach (Candidate c in candidates)
                if (c.TryDecrypt(pkt, out plaintext))
                    return c;
        return null;
    }
}
