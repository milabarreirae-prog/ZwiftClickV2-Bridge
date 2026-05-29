namespace ZwiftClickV2.Bridge.Protocol;

/// <summary>
/// Punto central para inyectar un payload auth real capturado por MITM.
///
/// 1) Pega el hex en <see cref="DefaultReplayHex"/>.
/// 2) Ejecuta: --hot-pair --auth-replay-hardcoded
/// </summary>
public static class AuthReplayPayloads
{
    // Pega aqui el payload real (hex), por ejemplo: "AA BB CC DD".
    private const string DefaultReplayHex = "";

    public static bool TryGetDefaultPayload(out byte[] payload)
    {
        string compact = new string(DefaultReplayHex.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (compact.Length == 0 || compact.Length % 2 != 0)
        {
            payload = Array.Empty<byte>();
            return false;
        }

        payload = new byte[compact.Length / 2];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = Convert.ToByte(compact.Substring(i * 2, 2), 16);

        return payload.Length > 0;
    }
}
