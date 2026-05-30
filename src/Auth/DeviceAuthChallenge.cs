namespace ZwiftClickV2.Bridge.Auth;

/// <summary>
/// Reto de autenticación que **genera el propio Zwift Click V2** y emite en CLARO por CH02,
/// envuelto en un header ZAP de 3 bytes <c>FF 03 00</c> (trama de ~85B). El cuerpo es el protobuf
/// de 82B que se reenvía **VERBATIM** a <c>/api/d-lock-service/device/authenticate</c>:
///
/// <code>
/// protobuf {
///   1: bytes  devicePubKeyCompressed (33B = 0x02/0x03 ‖ X)   ← pubkey EFÍMERA del device, por sesión
///   2: varint id                                              ← identificador ESTÁTICO del device
///   3: bytes  signature (40B)                                 ← firma fresca por sesión
/// }
/// </code>
///
/// RESUELTO (ver docs/protocol/unlock-flow.md): los tres campos los produce el dispositivo. El
/// bridge NO construye ni firma nada — solo localiza el blob, le quita el header y reenvía los 82B.
/// </summary>
public sealed class DeviceAuthChallenge
{
    /// <summary>El cuerpo protobuf de 82B, listo para POSTear verbatim.</summary>
    public required byte[] Body { get; init; }

    /// <summary>Campo 1: clave pública efímera del dispositivo, comprimida (33B).</summary>
    public required byte[] DevicePublicKeyCompressed { get; init; }

    /// <summary>Campo 2: identificador estático del dispositivo.</summary>
    public required ulong DeviceId { get; init; }

    /// <summary>Campo 3: firma/prueba de challenge (40B).</summary>
    public required byte[] Signature { get; init; }

    /// <summary>
    /// Si <paramref name="frame"/> contiene el protobuf del reto
    /// (<c>… 0A 21 02/03 &lt;pubkey&gt; 10 &lt;id&gt; 1A &lt;len&gt; &lt;sig&gt;</c>, p.ej. una
    /// notificación CH02 de 85B con header <c>FF 03 00</c>), lo parsea y devuelve true. Tolera
    /// cualquier prefijo: busca el comienzo del protobuf (tag <c>0A 21</c>) dentro del frame.
    /// </summary>
    public static bool TryParse(byte[] frame, out DeviceAuthChallenge? challenge)
    {
        challenge = null;
        for (int i = 0; i + 35 <= frame.Length; i++)
        {
            if (frame[i] != 0x0A || frame[i + 1] != 0x21) continue;               // campo 1: bytes, len 33
            if (frame[i + 2] != 0x02 && frame[i + 2] != 0x03) continue;            // prefijo punto comprimido
            byte[] pub = frame[(i + 2)..(i + 2 + 33)];
            int p = i + 2 + 33;

            if (p >= frame.Length || frame[p] != 0x10) continue;                   // campo 2: varint
            p++;
            ulong id = 0; int shift = 0; bool ok = false;
            while (p < frame.Length && shift < 64)
            {
                byte b = frame[p++];
                id |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) { ok = true; break; }
                shift += 7;
            }
            if (!ok) continue;

            if (p >= frame.Length || frame[p] != 0x1A) continue;                   // campo 3: bytes
            p++;
            if (p >= frame.Length) continue;
            int sigLen = frame[p++];
            if (p + sigLen > frame.Length) continue;
            byte[] sig = frame[p..(p + sigLen)];
            int end = p + sigLen;

            challenge = new DeviceAuthChallenge
            {
                Body = frame[i..end],   // protobuf contiguo (82B) — se reenvía verbatim
                DevicePublicKeyCompressed = pub,
                DeviceId = id,
                Signature = sig
            };
            return true;
        }
        return false;
    }
}
