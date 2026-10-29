using System.Collections.Concurrent;
using PHax.Core.Interfaces;

namespace PHax.Core.Services;

public sealed record ProtocolLogEntry(DateTime Utc, string Category, string Message);

/// <summary>
/// Thread-safe protocol log: keeps a bounded in-memory history for diagnostics and forwards entries to subscribers.
/// A per-session file can be attached with <see cref="BeginSessionFile"/>.
/// </summary>
public sealed class ProtocolLog : IProtocolLog
{
    private readonly ConcurrentQueue<ProtocolLogEntry> _entries = new();
    private readonly int _capacity;
    private readonly object _fileLock = new();
    private StreamWriter? _sessionFile;

    public ProtocolLog(int capacity = 2000) => _capacity = capacity;

    public event Action<ProtocolLogEntry>? EntryAdded;

    public void Log(string category, string message)
    {
        var entry = new ProtocolLogEntry(DateTime.UtcNow, category, message);
        _entries.Enqueue(entry);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _)) { }
        lock (_fileLock)
            _sessionFile?.WriteLine($"{entry.Utc:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message}");
        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<ProtocolLogEntry> Snapshot() => _entries.ToArray();

    public string BeginSessionFile(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{name}.log");
        lock (_fileLock)
        {
            _sessionFile?.Dispose();
            _sessionFile = new StreamWriter(path, append: false) { AutoFlush = true };
        }
        return path;
    }

    public void EndSessionFile()
    {
        lock (_fileLock)
        {
            _sessionFile?.Dispose();
            _sessionFile = null;
        }
    }
}

/// <summary>Discards everything.</summary>
public sealed class NullProtocolLog : IProtocolLog
{
    public static readonly NullProtocolLog Instance = new();
    public void Log(string category, string message) { }
}
