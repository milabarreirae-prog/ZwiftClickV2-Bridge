namespace ZwiftClickV2.Bridge.Bridge;

/// <summary>
/// Analiza paquetes misteriosos (ej. 85 bytes) que no se pueden descifrar
/// con la clave de sesión actual. Intenta fuerza bruta de contadores,
/// diferentes modos de cifrado, y registra patrones.
/// </summary>
public class MysteryPacketAnalyzer
{
    private readonly List<byte[]> _capturedPackets = new();
    private int _packetCount;

    /// <summary>
    /// Analiza un paquete capturado que falló el descifrado normal.
    /// </summary>
    public void Analyze(byte[] data)
    {
        _packetCount++;
        _capturedPackets.Add(data);

        Console.WriteLine($"   🔍 [Mystery #{_packetCount}] {data.Length}B: {BitConverter.ToString(data.Take(32).ToArray()).Replace("-", "")}...");

        // Detectar patrones conocidos
        DetectPatterns(data);

        // Si es ~85B, intentar fuerza bruta de counters
        if (data.Length >= 80 && data.Length <= 90)
        {
            Console.WriteLine($"   🔬 Paquete de {data.Length}B — posible paquete de evento");
            Analyze85BytePacket(data);
        }
    }

    private void DetectPatterns(byte[] data)
    {
        // ¿Empieza con secuencia?
        if (data.Length >= 4)
        {
            uint possibleSeq = BitConverter.ToUInt32(data, 0);
            if (possibleSeq < 1000)
                Console.WriteLine($"   📊 Posible seq LE en [0:4]: {possibleSeq}");
        }

        // ¿Contiene "RideOn"?
        for (int i = 0; i <= data.Length - 6; i++)
        {
            if (data[i] == 'R' && data[i + 1] == 'i' && data[i + 2] == 'd' &&
                data[i + 3] == 'e' && data[i + 4] == 'O' && data[i + 5] == 'n')
            {
                Console.WriteLine($"   🏷️  Contiene 'RideOn' en offset {i}");
                if (i + 7 < data.Length)
                    Console.WriteLine($"      Sufijo: {data[i + 6]:X2} {data[i + 7]:X2}");
            }
        }

        // ¿Tiene bytes repetidos (posible padding)?
        var groups = data.GroupBy(b => b).OrderByDescending(g => g.Count()).Take(3);
        foreach (var g in groups)
            if (g.Count() > 5)
                Console.WriteLine($"   🔁 Byte 0x{g.Key:X2} repetido {g.Count()} veces");
    }

    private void Analyze85BytePacket(byte[] data)
    {
        // Probar extraer el sequence number de diferentes posiciones
        for (int seqOffset = 0; seqOffset <= 4; seqOffset++)
        {
            if (data.Length < seqOffset + 4) continue;
            uint seq = BitConverter.ToUInt32(data, seqOffset);
            Console.WriteLine($"      Seq@{seqOffset}: {seq}");
        }

        // Buscar posibles tags (últimos 4 bytes)
        if (data.Length >= 4)
        {
            byte[] last4 = data.AsSpan(data.Length - 4).ToArray();
            Console.WriteLine($"      Últimos 4B (posible tag): {BitConverter.ToString(last4).Replace("-", "")}");
        }

        // Buscar posibles nonces (primeros 4 bytes después del seq)
        if (data.Length >= 8)
        {
            byte[] maybeNonce = data.AsSpan(4, 4).ToArray();
            Console.WriteLine($"      Bytes 4-7 (posible nonce): {BitConverter.ToString(maybeNonce).Replace("-", "")}");
        }
    }

    /// <summary>
    /// Retorna todos los paquetes capturados para análisis offline.
    /// </summary>
    public IReadOnlyList<byte[]> GetCapturedPackets() => _capturedPackets.AsReadOnly();

    /// <summary>
    /// Exporta los paquetes capturados como hex dump.
    /// </summary>
    public string ExportHexDump()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _capturedPackets.Count; i++)
        {
            sb.AppendLine($"--- Packet #{i + 1} ({_capturedPackets[i].Length}B) ---");
            sb.AppendLine(BitConverter.ToString(_capturedPackets[i]).Replace("-", " "));
            sb.AppendLine();
        }
        return sb.ToString();
    }
}