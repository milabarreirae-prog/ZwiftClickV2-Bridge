using System.Text.Json;

namespace ZwiftClickV2.Bridge.Logging;

/// <summary>
/// Logger estructurado que escribe eventos en formato JSON con timestamps precisos.
/// Cada evento tiene: timestamp, event, direction, characteristic, payload_hex, payload_size, notes.
/// </summary>
public class StructuredLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly string _filePath;
    private readonly object _lock = new();
    private bool _firstEntry = true;

    public StructuredLogger(string? filePath = null)
    {
        Directory.CreateDirectory("logs");
        _filePath = filePath ?? Path.Combine("logs", $"session_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        _writer = new StreamWriter(_filePath, append: false);
        _writer.WriteLine("["); // Abrir array JSON
        Console.WriteLine($"[Logger] 📝 Sesión: {_filePath}");
    }

    public void Log(string eventType, string direction, string characteristic,
        byte[] payload, string notes = "")
    {
        lock (_lock)
        {
            if (!_firstEntry) _writer.WriteLine(",");
            _firstEntry = false;

            var entry = new
            {
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                @event = eventType,
                direction,
                characteristic,
                payload_hex = BitConverter.ToString(payload).Replace("-", ""),
                payload_size = payload.Length,
                notes
            };

            string json = JsonSerializer.Serialize(entry);
            _writer.Write($"  {json}");
            _writer.Flush();
        }

        // También loguear a consola
        string arrow = direction == "tx" ? "📤" : "📥";
        Console.WriteLine($"{arrow} [{characteristic}] {eventType}: {payload.Length}B {notes}");
    }

    public void LogInfo(string message)
    {
        lock (_lock)
        {
            if (!_firstEntry) _writer.WriteLine(",");
            _firstEntry = false;

            var entry = new
            {
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                @event = "info",
                message
            };
            _writer.Write($"  {JsonSerializer.Serialize(entry)}");
            _writer.Flush();
        }
        Console.WriteLine($"ℹ️  {message}");
    }

    public void LogError(string message)
    {
        lock (_lock)
        {
            if (!_firstEntry) _writer.WriteLine(",");
            _firstEntry = false;

            var entry = new
            {
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                @event = "error",
                message
            };
            _writer.Write($"  {JsonSerializer.Serialize(entry)}");
            _writer.Flush();
        }
        Console.WriteLine($"❌ {message}");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.WriteLine();
            _writer.WriteLine("]");
            _writer.Dispose();
        }
        Console.WriteLine($"[Logger] ✅ Log guardado en: {_filePath}");
    }

    public string FilePath => _filePath;
}