using System.Text;

namespace ZwiftClickV2.Bridge.Protocol;

/// <summary>
/// Parsea respuestas de handshake del Zwift Click V2.
/// Acepta múltiples formatos de sufijo: 01 01, 01 02, 02 03, 00 09
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
        new byte[] { 0x02, 0x03 },  // V2 status/event
        new byte[] { 0x00, 0x09 },  // V1 alternative
    };

    /// <summary>
    /// Clasifica una respuesta de handshake.
    /// </summary>
    public static ResponseType Classify(byte[] response)
    {
        if (response == null || response.Length < 8)
            return ResponseType.Unknown;

        // ¿Es "RideOn" + sufijo?
        if (response.Length >= 6 &&
            response[0] == 'R' && response[1] == 'i' && response[2] == 'd' &&
            response[3] == 'e' && response[4] == 'O' && response[5] == 'n')
        {
            // Extraer sufijo si existe
            if (response.Length >= 8)
            {
                byte[] suffix = { response[6], response[7] };

                // ¿Es un estado de rechazo?
                if (suffix[0] == 0x02 && suffix[1] == 0x03)
                {
                    // Revisar si después viene clave pública o datos de estado
                    if (response.Length >= 73 && response[8] == 0x04)
                        return ResponseType.PublicKey;
                    return ResponseType.StatusMessage;
                }

                if (KnownSuffixes.Any(s => s[0] == suffix[0] && s[1] == suffix[1]))
                    return ResponseType.PublicKey; // Asumir que es clave
            }
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
}