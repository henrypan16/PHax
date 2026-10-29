using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using PHax.Core.Ipc;

namespace PHax.App.Services;

/// <summary>Named-pipe client for the PHax service with automatic reconnection.</summary>
public sealed class ServiceClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<PipeEnvelope>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private Task? _loop;

    public bool IsConnected { get; private set; }
    public event Action<bool>? ConnectionChanged;
    public event Action<PipeEnvelope>? EventReceived;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000, ct);
                _pipe = pipe;
                _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                SetConnected(true);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false));
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;
                    PipeEnvelope? env;
                    try { env = PipeProtocol.Deserialize(line); } catch (System.Text.Json.JsonException) { continue; }
                    if (env is null) continue;
                    if (env.Kind == EnvelopeKind.Response && _pending.TryRemove(env.Id, out var tcs)) tcs.TrySetResult(env);
                    else if (env.Kind == EnvelopeKind.Event) EventReceived?.Invoke(env);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { }
            SetConnected(false);
            foreach (var p in _pending) { if (_pending.TryRemove(p.Key, out var t)) t.TrySetException(new IOException("Service connection lost.")); }
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
            _writer = null;
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        ConnectionChanged?.Invoke(value);
    }

    public async Task<T?> RequestAsync<T>(string type, object? payload = null, TimeSpan? timeout = null)
    {
        var response = await SendAsync(type, payload, timeout);
        return PipeProtocol.FromElement<T>(response.Payload);
    }

    public async Task<PipeEnvelope> SendAsync(string type, object? payload = null, TimeSpan? timeout = null)
    {
        var writer = _writer ?? throw new InvalidOperationException("The PHax service is not running.");
        var env = new PipeEnvelope
        {
            Kind = EnvelopeKind.Request,
            Id = Guid.NewGuid(),
            Type = type,
            Payload = payload is null ? null : PipeProtocol.ToElement(payload),
        };
        var tcs = new TaskCompletionSource<PipeEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[env.Id] = tcs;
        await _writeLock.WaitAsync();
        try { await writer.WriteLineAsync(PipeProtocol.Serialize(env)); }
        finally { _writeLock.Release(); }
        var response = await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30));
        if (response.Error is { } error) throw new InvalidOperationException(error);
        return response;
    }

    /// <summary>Sends a command, ignoring failures when the service is unavailable.</summary>
    public async Task<bool> TrySendAsync(string type, object? payload = null)
    {
        if (!IsConnected) return false;
        try { await SendAsync(type, payload); return true; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _pipe?.Dispose(); } catch { }
        if (_loop is not null) { try { await _loop; } catch { } }
    }
}
