namespace ZwiftClickV2.Bridge.Protocol;

/// <summary>
/// Constantes de wire del handshake/unlock del Zwift Click V2, confirmadas por captura
/// BLE+HTTP correlacionada (ver docs/protocol/phaseC-dlock-auth.md).
/// </summary>
public static class ZapCommands
{
    /// <summary>Prefijo "RideOn" (6 bytes ASCII) que abre todo handshake ZAP.</summary>
    public static readonly byte[] RideOn = "RideOn"u8.ToArray();

    /// <summary>
    /// Prefijo de protocolo del Click V2 = <c>02 03</c>. El handshake real que escribe la app
    /// es <c>"RideOn" 02 03 + localPubKey[64]</c> en CH03. El <c>01 02 / 01 03</c> es V1 (Play 2023).
    /// </summary>
    public static readonly byte[] V2Prefix = { 0x02, 0x03 };

    /// <summary>Prefijo de protocolo V1 (Zwift Play 2023). Conservado solo para compatibilidad.</summary>
    public static readonly byte[] V1Prefix = { 0x01, 0x02 };

    /// <summary>
    /// Comando de unlock <c>FF 04 00</c> que la app escribe en CH03 ~11 ms DESPUÉS de que el
    /// servidor d-lock devuelve 204. NO es un unlock local: solo es válido tras la validación
    /// del servidor con el token de la cuenta Zwift. Ver docs/protocol/phaseC-dlock-auth.md.
    /// </summary>
    public static readonly byte[] UnlockConfirm = { 0xFF, 0x04, 0x00 };

    /// <summary>
    /// Construye el payload del handshake: <c>"RideOn" + prefix + pubKey</c>.
    /// La pubkey se envía cruda de 64B (X‖Y), sin el prefijo 0x04.
    /// </summary>
    public static byte[] BuildHandshake(byte[] prefix, byte[] publicKey)
    {
        byte[] pubWire = publicKey.Length == 65 && publicKey[0] == 0x04
            ? publicKey.AsSpan(1, 64).ToArray()
            : publicKey;

        byte[] result = new byte[RideOn.Length + prefix.Length + pubWire.Length];
        Buffer.BlockCopy(RideOn, 0, result, 0, RideOn.Length);
        Buffer.BlockCopy(prefix, 0, result, RideOn.Length, prefix.Length);
        Buffer.BlockCopy(pubWire, 0, result, RideOn.Length + prefix.Length, pubWire.Length);
        return result;
    }
}
