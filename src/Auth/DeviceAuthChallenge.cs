using ZwiftClickV2.Bridge.Crypto;

namespace ZwiftClickV2.Bridge.Auth;

/// <summary>
/// Cuerpo del request <c>POST /api/d-lock-service/device/authenticate</c>, decodificado
/// de la captura MITM (ver docs/protocol/phaseC-dlock-auth.md):
///
/// <code>
/// protobuf {
///   1: bytes  devicePubKeyCompressed (33B = 0x02/0x03 ‖ X)   ← del handshake BLE (campo 1)
///   2: varint id                                              ← lo genera el DISPOSITIVO
///   3: bytes  signature (40B)                                 ← lo genera el DISPOSITIVO
/// }
/// </code>
///
/// ⚠️ DESCONOCIDO QUE BLOQUEA EL UNLOCK COMPLETO: el origen exacto de los campos 2 (id) y
/// 3 (firma 40B). El campo 1 sale del handshake; los campos 2 y 3 los emite el dispositivo y
/// la app oficial los lee por BLE (¿handshake? ¿CH100/101/102?). Hasta resolverlo no se puede
/// generar un request válido. Resolver vía decompile de la ruta de auth (FUN_14050d1d0
/// "Received auth challenge") y/o recaptura BLE+HTTP correlacionada.
/// </summary>
public sealed class DeviceAuthChallenge
{
    /// <summary>Clave pública del dispositivo, comprimida a 33B (campo 1).</summary>
    public required byte[] DevicePublicKeyCompressed { get; init; }

    /// <summary>Identificador del dispositivo (campo 2). Origen no resuelto.</summary>
    public required ulong DeviceId { get; init; }

    /// <summary>Firma/prueba de challenge de 40B (campo 3). Origen no resuelto.</summary>
    public required byte[] Signature { get; init; }

    /// <summary>
    /// Construye un challenge a partir de la pubkey cruda de 64B del dispositivo (del handshake)
    /// más el id y la firma que el dispositivo debe haber provisto por BLE.
    /// </summary>
    public static DeviceAuthChallenge FromDevicePublicKey(byte[] devicePublicKey64, ulong deviceId, byte[] signature40)
    {
        if (signature40 == null || signature40.Length != 40)
            throw new ArgumentException("La firma del dispositivo debe ser de 40 bytes", nameof(signature40));

        return new DeviceAuthChallenge
        {
            DevicePublicKeyCompressed = EcPoint.Compress(devicePublicKey64),
            DeviceId = deviceId,
            Signature = signature40
        };
    }

    /// <summary>
    /// Serializa el cuerpo protobuf del request <c>device/authenticate</c>.
    /// Campo 1 = len-delimited (tag 0x0A), campo 2 = varint (tag 0x10), campo 3 = len-delimited (tag 0x1A).
    /// </summary>
    public byte[] ToProtobuf()
    {
        if (DevicePublicKeyCompressed.Length != 33)
            throw new InvalidOperationException("La pubkey comprimida debe ser de 33 bytes");
        if (Signature.Length != 40)
            throw new InvalidOperationException("La firma debe ser de 40 bytes");

        using var ms = new MemoryStream();

        // Campo 1: bytes (wire type 2)
        ms.WriteByte(0x0A);
        WriteVarint(ms, (ulong)DevicePublicKeyCompressed.Length);
        ms.Write(DevicePublicKeyCompressed, 0, DevicePublicKeyCompressed.Length);

        // Campo 2: varint (wire type 0)
        ms.WriteByte(0x10);
        WriteVarint(ms, DeviceId);

        // Campo 3: bytes (wire type 2)
        ms.WriteByte(0x1A);
        WriteVarint(ms, (ulong)Signature.Length);
        ms.Write(Signature, 0, Signature.Length);

        return ms.ToArray();
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            stream.WriteByte(b);
        } while (value != 0);
    }
}
