using System.Security.Cryptography;
using ZwiftClickV2.Bridge.Crypto;
using ZwiftClickV2.Bridge.Logging;

namespace ZwiftClickV2.Bridge.Bridge;

/// <summary>
/// Intenta descifrar paquetes capturados (ej. 85 bytes en CH02)
/// usando fuerza bruta de contadores sobre la clave de sesión.
/// </summary>
public class PacketDecryptor
{
    private readonly StructuredLogger _logger;
    private ZPEncryptionV2? _zp;

    public List<byte[]> CapturedPackets { get; } = new();

    public PacketDecryptor(StructuredLogger logger) => _logger = logger;

    public void SetSessionKey(ZPEncryptionV2 zp)
    {
        _zp = zp;
        _logger.LogInfo("🔑 Clave de sesión configurada para PacketDecryptor");
    }

    public void Capture(byte[] data)
    {
        CapturedPackets.Add(data);
        _logger.Log("packet_captured", "rx", "CH02", data, $"#{CapturedPackets.Count} ({data.Length}B)");
    }

    public Task<List<DecryptAttempt>> DecryptAllAsync()
    {
        var results = new List<DecryptAttempt>();

        foreach (var packet in CapturedPackets)
        {
            _logger.LogInfo($"\n🔬 Intentando descifrar paquete ({packet.Length}B)");
            _logger.LogInfo($"   Hex: {BitConverter.ToString(packet.Take(32).ToArray()).Replace("-", "")}...");

            var result = TryDecrypt(packet);
            results.Add(result);

            if (result.Success)
            {
                _logger.LogInfo($"   ✅ ¡DESCIFRADO! Counter={result.Counter}");
                _logger.LogInfo($"   Plaintext ({result.Plaintext!.Length}B): {BitConverter.ToString(result.Plaintext).Replace("-", "")}");
                TryParseProtobuf(result.Plaintext);
            }
            else
            {
                _logger.LogInfo($"   ❌ No se pudo descifrar ({result.AttemptsFailed} intentos)");
            }
        }
        return Task.FromResult(results);
    }

    private DecryptAttempt TryDecrypt(byte[] packet)
    {
        if (_zp == null) return new DecryptAttempt(false, null, null, MaxCounter);

        for (uint counter = 0; counter <= 1000; counter++)
        {
            try
            {
                _zp.ResetCounters();
                // Avanzar el txCounter para saltar al counter deseado
                for (uint i = 0; i < counter; i++)
                    _zp.Encrypt(Array.Empty<byte>());

                byte[] plaintext = _zp.Decrypt(packet);
                if (IsValidPlaintext(plaintext))
                    return new DecryptAttempt(true, counter, plaintext, 0);
            }
            catch (CryptographicException) { /* Tag mismatch */ }
            catch { break; }
        }

        return new DecryptAttempt(false, null, null, MaxCounter);
    }

    private const uint MaxCounter = 1001;

    private static bool IsValidPlaintext(byte[] data)
    {
        if (data == null || data.Length == 0) return false;
        byte first = data[0];
        if (first == 0x08 || first == 0x10 || first == 0x12 ||
            first == 0x18 || first == 0x1A || first == 0x20 || first == 0x22)
            return true;
        int ascii = 0;
        foreach (byte b in data)
        {
            if (b >= 32 && b < 127) ascii++;
            else ascii = 0;
            if (ascii >= 4) return true;
        }
        int nonZero = data.Count(b => b != 0);
        return nonZero > data.Length * 0.7;
    }

    private void TryParseProtobuf(byte[] data)
    {
        try
        {
            var fields = new List<string>();
            int offset = 0;
            while (offset < data.Length - 1)
            {
                byte tag = data[offset];
                int fn = tag >> 3;
                int wt = tag & 0x07;
                offset++;
                if (wt == 0) // Varint
                {
                    ulong value = 0; int shift = 0;
                    while (offset < data.Length && shift < 64)
                    {
                        byte b = data[offset++];
                        value |= (ulong)(b & 0x7F) << shift;
                        if ((b & 0x80) == 0) break;
                        shift += 7;
                    }
                    fields.Add($"{fn}:{value}");
                }
                else if (wt == 2) // Length-delimited
                {
                    if (offset >= data.Length) break;
                    int len = data[offset++];
                    offset += len;
                    fields.Add($"{fn}:bytes[{len}]");
                }
                else break;
            }
            _logger.LogInfo($"   📦 Protobuf fields: {string.Join(", ", fields)}");
        }
        catch (Exception ex)
        {
            _logger.LogInfo($"   ⚠️  Parseo Protobuf falló: {ex.Message}");
        }
    }

    public record DecryptAttempt(bool Success, long? Counter, byte[]? Plaintext, long AttemptsFailed);
}