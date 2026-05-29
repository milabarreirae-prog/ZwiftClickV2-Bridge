using System.Text;

namespace ZwiftClickV2.Bridge.Protocol;

/// <summary>
/// Parsea respuestas de handshake del Zwift Click V2.
/// Acepta múltiples formatos de sufijo: 01 01, 01 02, 01 03, 02 03, 00 09
/// y detecta códigos de error/rechazo.
/// </summary>
public static class HandshakeParser
{
    public enum ResponseType
    {
        Unknown,
        PublicKey,       // Contiene clave pública EC P-256
        StatusMessage,   // Datos de estado/batería (rechazo)
        ErrorCode        // Código de error específico
    }

    /// <summary>
    /// Sufijos de respuesta conocidos después de "RideOn".
    /// </summary>
    public static readonly HashSet<byte[]> KnownSuffixes = new()
    {
        new byte[] { 0x01, 0x01 },  // V1 handshake accept
        new byte[] { 0x01, 0x02 },  // V1/V2 handshake
        new byte[] { 0x01, 0x03 },  // V2 handshake response
        new byte[] { 0x02, 0x03 },  // V2 status/event
        new byte[] { 0x00, 0x09 },  // V1 alternative
    };

    /// <summary>
    /// Clasifica una respuesta de handshake.
    /// </summary>
    public static ResponseType Classify(byte[] response)
    {
        if (response == null || response.Length == 0)
            return ResponseType.Unknown;

        // Código de rechazo 58 02 — confirmado empíricamente en fuzzing V2.
        // Se chequea antes que el wrapper "RideOn" porque puede llegar como payload crudo.
        if (response.Length >= 2 && response[0] == 0x58 && response[1] == 0x02)
            return ResponseType.ErrorCode;

        // Patrón Protobuf battery sin prefijo RideOn — 08 00 10 64 18 (Field 2 = 100%).
        if (response.Length >= 5 &&
            response[0] == 0x08 && response[1] == 0x00 &&
            response[2] == 0x10 && response[3] == 0x64 && response[4] == 0x18)
            return ResponseType.StatusMessage;

        // ¿Es "RideOn" + sufijo?
        if (response.Length >= 8 &&
            response[0] == 'R' && response[1] == 'i' && response[2] == 'd' &&
            response[3] == 'e' && response[4] == 'O' && response[5] == 'n')
        {
            byte[] suffix = { response[6], response[7] };

            // ¿Es un estado de rechazo?
            if (suffix[0] == 0x02 && suffix[1] == 0x03)
            {
                return ResponseType.StatusMessage;
            }

            if (suffix[0] == 0x01 && suffix[1] == 0x03 && response.Length >= 72)
                return ResponseType.PublicKey;

            if (KnownSuffixes.Any(s => s[0] == suffix[0] && s[1] == suffix[1]))
                return ResponseType.PublicKey; // Asumir que es clave
        }

        // ¿Empieza con 0x04 directamente? → clave pública sin prefijo RideOn
        if (response.Length >= 65 && response[0] == 0x04)
            return ResponseType.PublicKey;

        return ResponseType.Unknown;
    }

    /// <summary>
    /// Extrae la clave pública EC (65 bytes) de una respuesta de handshake.
    /// </summary>
    public static byte[]? ExtractPublicKey(byte[] response)
    {
        byte[]? rawPublicKey = ExtractRawPublicKey(response);
        if (rawPublicKey != null)
        {
            byte[] publicKey = new byte[65];
            publicKey[0] = 0x04;
            Array.Copy(rawPublicKey, 0, publicKey, 1, 64);
            return publicKey;
        }

        if (response == null) return null;

        // Buscar 0x04 seguido de 64 bytes con alta entropía
        for (int offset = 0; offset <= response.Length - 65; offset++)
        {
            if (response[offset] != 0x04) continue;

            byte[] candidate = response.AsSpan(offset, 65).ToArray();
            int nonZero = candidate.Skip(1).Count(b => b != 0);
            if (nonZero >= 40) // Suficiente entropía
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Extrae la clave pública EC cruda (64 bytes X||Y, sin 0x04) de una respuesta V2.
    /// Soporta tanto el formato observado RideOn+01 03+64B como una variante con 0x04 explícito.
    /// </summary>
    public static byte[]? ExtractRawPublicKey(byte[] response)
    {
        if (response == null) return null;

        if (response.Length >= 8 &&
            response[0] == 'R' && response[1] == 'i' && response[2] == 'd' &&
            response[3] == 'e' && response[4] == 'O' && response[5] == 'n' &&
            response[6] == 0x01 && response[7] == 0x03)
        {
            if (response.Length >= 73 && response[8] == 0x04)
                return response.AsSpan(9, 64).ToArray();

            if (response.Length >= 72)
                return response.AsSpan(8, 64).ToArray();
        }

        if (response.Length >= 65 && response[0] == 0x04)
            return response.AsSpan(1, 64).ToArray();

        return null;
    }

    /// <summary>
    /// Extrae el sufijo de 2 bytes después de "RideOn".
    /// </summary>
    public static byte[]? ExtractSuffix(byte[] response)
    {
        if (response == null || response.Length < 8) return null;
        if (response[0] == 'R' && response[1] == 'i' && response[2] == 'd' &&
            response[3] == 'e' && response[4] == 'O' && response[5] == 'n')
        {
            return new byte[] { response[6], response[7] };
        }
        return null;
    }

    /// <summary>
    /// Intenta extraer texto ASCII visible de la respuesta.
    /// </summary>
    public static string ExtractAscii(byte[] data)
    {
        return Encoding.ASCII.GetString(data.Where(b => b >= 32 && b < 127).ToArray());
    }

    /// <summary>
    /// Determina si la respuesta es un mensaje de rechazo conocido
    /// (datos de batería/estado en lugar de clave pública).
    /// </summary>
    public static bool LooksLikeRejection(byte[] response)
    {
        if (response == null || response.Length < 10) return false;

        // Patrón: "RideOn" + 02 03 + datos protobuf
        if (response.Length >= 8 &&
            response[0] == 'R' && response[1] == 'i' && response[2] == 'd' &&
            response[3] == 'e' && response[4] == 'O' && response[5] == 'n' &&
            response[6] == 0x02 && response[7] == 0x03)
        {
            byte[] afterHeader = response.Skip(8).ToArray();

            // Batería: 0x10 = field 2 varint, 0x64 = 100%
            if (afterHeader.Length >= 2 && afterHeader[0] == 0x10 && afterHeader[1] == 0x64)
                return true;

            // Código de estado 0x58 = field 11 varint
            if (afterHeader.Length >= 2 && afterHeader[0] == 0x58)
                return true;

            // Cualquier protobuf con fields bajos (0x08, 0x10, 0x18, 0x20)
            if (afterHeader.Length >= 1 &&
                (afterHeader[0] == 0x08 || afterHeader[0] == 0x10 ||
                 afterHeader[0] == 0x18 || afterHeader[0] == 0x20))
                return true;
        }
        // 58 02 crudo sin wrapper RideOn — código de rechazo confirmado en fuzzing
        if (response.Length >= 2 && response[0] == 0x58 && response[1] == 0x02)
            return true;

        return false;
    }
}
