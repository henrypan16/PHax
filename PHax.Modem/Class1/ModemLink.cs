using System.Text;
using System.Threading.Channels;
using PHax.Core.Interfaces;

namespace PHax.Modem.Class1;

public sealed class ModemDisconnectedException(string message, Exception? inner = null) : IOException(message, inner);

public sealed class ModemNotRespondingException(string message) : IOException(message);

public sealed record AtResponse(string Result, IReadOnlyList<string> Lines, bool TimedOut)
{
    public bool IsOk => Result == "OK";
    public string Info => string.Join(" ", Lines);
}

/// <summary>
/// Byte-level link to the modem: a background pump fills a buffer from the transport; callers read lines or raw
/// bytes with timeouts. All AT traffic is reported through <see cref="Traffic"/> (data payloads are summarised, never dumped).
/// </summary>
public sealed class ModemLink : IAsyncDisposable
{
    public static readonly string[] FinalResults =
        ["OK", "CONNECT", "ERROR", "NO CARRIER", "BUSY", "NO DIALTONE", "NO DIAL TONE", "NO ANSWER", "+FCERROR", "FCERROR", "DELAYED", "BLACKLISTED"];

    private readonly ISerialTransport _transport;
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private byte[] _current = [];
    private int _currentPos;
    private readonly StringBuilder _line = new();
    // A result code ends with CR LF; when a line was terminated by CR, the LF that follows must not be taken as data.
    private bool _skipLf;

    public event Action<string, string>? Traffic;
    public event Action<string>? UnsolicitedLine;
    public event Action<Exception>? Disconnected;

    public bool IsConnected { get; private set; } = true;
    public ISerialTransport Transport => _transport;

    public ModemLink(ISerialTransport transport)
    {
        _transport = transport;
        _pump = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _transport.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (n <= 0) continue;
                _chunks.Writer.TryWrite(buffer.AsSpan(0, n).ToArray());
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
            // Intentional shutdown (the transport may have been closed under the pending read): not a disconnect.
            _chunks.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            var wrapped = new ModemDisconnectedException($"Modem connection lost on {_transport.Name}: {ex.Message}", ex);
            _chunks.Writer.TryComplete(wrapped);
            Disconnected?.Invoke(wrapped);
        }
    }

    /// <summary>Reads one byte, or returns -1 on timeout.</summary>
    public async ValueTask<int> ReadByteAsync(TimeSpan timeout, CancellationToken ct)
    {
        int b = await ReadRawByteAsync(timeout, ct).ConfigureAwait(false);
        if (_skipLf)
        {
            _skipLf = false;
            if (b == '\n') b = await ReadRawByteAsync(timeout, ct).ConfigureAwait(false);
        }
        return b;
    }

    private async ValueTask<int> ReadRawByteAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_currentPos < _current.Length) return _current[_currentPos++];
        if (_chunks.Reader.TryRead(out var chunk))
        {
            _current = chunk;
            _currentPos = 0;
            return _current[_currentPos++];
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            _current = await _chunks.Reader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
            _currentPos = 0;
            return _current[_currentPos++];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return -1;
        }
        catch (ChannelClosedException ex)
        {
            throw ex.InnerException as ModemDisconnectedException ?? new ModemDisconnectedException("Modem link closed.", ex);
        }
    }

    /// <summary>Discards everything buffered so far.</summary>
    public void Flush()
    {
        _current = [];
        _currentPos = 0;
        while (_chunks.Reader.TryRead(out _)) { }
        _line.Clear();
        _skipLf = false;
        _transport.DiscardInput();
    }

    /// <summary>Reads the next non-empty line within <paramref name="timeout"/>, or null.</summary>
    public async Task<string?> ReadLineAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            int b = await ReadByteAsync(remaining, ct).ConfigureAwait(false);
            if (b < 0) return null;
            if (b == '\n' || b == '\r')
            {
                if (_line.Length == 0) continue;
                var line = _line.ToString().Trim();
                _line.Clear();
                _skipLf = b == 13;
                if (line.Length == 0) continue;
                Traffic?.Invoke("<", line);
                return line;
            }
            if (b >= 0x20 || b == '\t') _line.Append((char)b);
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        try
        {
            await _transport.WriteAsync(data, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            IsConnected = false;
            throw new ModemDisconnectedException($"Write to {_transport.Name} failed: {ex.Message}", ex);
        }
    }

    public Task WriteCommandAsync(string command, CancellationToken ct)
    {
        Traffic?.Invoke(">", command);
        return WriteAsync(Encoding.ASCII.GetBytes(command + "\r"), ct);
    }

    public void ReportData(string direction, string summary) => Traffic?.Invoke(direction, summary);

    /// <summary>Sends a command and collects response lines until a final result code.</summary>
    public async Task<AtResponse> CommandAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        await WriteCommandAsync(command, ct).ConfigureAwait(false);
        return await ReadResultAsync(timeout, ct, command).ConfigureAwait(false);
    }

    public async Task<AtResponse> ReadResultAsync(TimeSpan timeout, CancellationToken ct, string? echo = null)
    {
        var lines = new List<string>();
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            var line = remaining > TimeSpan.Zero ? await ReadLineAsync(remaining, ct).ConfigureAwait(false) : null;
            if (line is null) return new AtResponse("", lines, TimedOut: true);
            if (echo is not null && line.Equals(echo, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsUnsolicited(line))
            {
                UnsolicitedLine?.Invoke(line);
                continue;
            }
            var final = MatchFinal(line);
            if (final is not null) return new AtResponse(final, lines, false);
            lines.Add(line);
        }
    }

    public static string? MatchFinal(string line)
    {
        var upper = line.ToUpperInvariant();
        foreach (var r in FinalResults)
            if (upper == r || (r == "CONNECT" && upper.StartsWith("CONNECT", StringComparison.Ordinal)))
                return r == "FCERROR" ? "+FCERROR" : r == "NO DIAL TONE" ? "NO DIALTONE" : r;
        return null;
    }

    public static bool IsUnsolicited(string line)
    {
        var upper = line.ToUpperInvariant();
        return upper == "RING" || upper.StartsWith("NMBR", StringComparison.Ordinal) || upper.StartsWith("NAME", StringComparison.Ordinal)
               || upper.StartsWith("DATE", StringComparison.Ordinal) || upper.StartsWith("TIME", StringComparison.Ordinal)
               || upper.StartsWith("DRING", StringComparison.Ordinal) || upper.StartsWith("CALLER NUMBER", StringComparison.Ordinal);
    }

    /// <summary>Marks the link as shutting down so errors from closing the transport are not reported as disconnects.</summary>
    public void BeginShutdown() => _cts.Cancel();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        // Never let a stuck driver read block shutdown or a modem re-open.
        try { await _pump.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { /* pump faults are reported through Disconnected */ }
    }
}
