using System.IO.Ports;
using System.Threading.Channels;
using PHax.Core.Interfaces;

namespace PHax.Modem.Serial;

/// <summary>
/// System.IO.Ports transport for USB virtual COM ports (8N1, optional RTS/CTS flow control).
/// Reads run on a dedicated thread using synchronous reads with a 250 ms timeout: asynchronous reads on some USB
/// serial drivers ignore the timeout and cannot be cancelled, which made closing an idle port hang.
/// </summary>
public sealed class SerialPortTransport : ISerialTransport
{
    private readonly int _baudRate;
    private readonly bool _hardwareFlow;
    private SerialPort? _port;
    private Thread? _reader;
    private Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private volatile bool _closing;
    private byte[] _chunk = [];
    private int _chunkPos;

    public SerialPortTransport(string portName, int baudRate, bool hardwareFlowControl)
    {
        Name = portName;
        _baudRate = baudRate;
        _hardwareFlow = hardwareFlowControl;
    }

    public string Name { get; }
    public bool IsOpen => _port?.IsOpen == true;
    public bool IsSimulated => false;

    public Task OpenAsync(CancellationToken ct)
    {
        Close();
        var port = new SerialPort(Name, _baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = _hardwareFlow ? Handshake.RequestToSend : Handshake.None,
            DtrEnable = true,
            ReadTimeout = 250,
            WriteTimeout = 30_000,
            ReadBufferSize = 64 * 1024,
            WriteBufferSize = 64 * 1024,
        };
        if (!_hardwareFlow) port.RtsEnable = true;
        try
        {
            port.Open();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
        {
            port.Dispose();
            throw new IOException(ex is UnauthorizedAccessException
                ? $"{Name} is in use by another program (only the PHax service may own the modem port)."
                : $"Cannot open {Name}: {ex.Message}", ex);
        }
        _port = port;
        _closing = false;
        _chunk = [];
        _chunkPos = 0;
        var channel = _incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _reader = new Thread(() => ReadLoop(port, channel)) { IsBackground = true, Name = $"PHax serial reader {Name}" };
        _reader.Start();
        return Task.CompletedTask;
    }

    private void ReadLoop(SerialPort port, Channel<byte[]> channel)
    {
        var buffer = new byte[4096];
        while (!_closing)
        {
            try
            {
                int n = port.Read(buffer, 0, buffer.Length);
                if (n > 0) channel.Writer.TryWrite(buffer.AsSpan(0, n).ToArray());
            }
            catch (TimeoutException)
            {
                // No data within 250 ms; loop to observe _closing.
            }
            catch (OperationCanceledException) when (!_closing && port.IsOpen)
            {
                // ERROR_OPERATION_ABORTED: DiscardInBuffer (PurgeComm with PURGE_RXABORT) cancels the pending read.
                // The port is fine; keep reading.
            }
            catch (IOException ex) when (!_closing && port.IsOpen && ex.HResult == unchecked((int)0x800703E3))
            {
                // Same abort surfaced as an IOException (HRESULT for ERROR_OPERATION_ABORTED).
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
            {
                channel.Writer.TryComplete(_closing ? null : new IOException($"{Name}: {ex.Message}", ex));
                return;
            }
        }
        channel.Writer.TryComplete();
    }

    public void Close()
    {
        _closing = true;
        var port = _port;
        _port = null;
        if (port is not null)
        {
            // Closing the port also unblocks a pending Read on the reader thread.
            try { port.Close(); } catch (IOException) { /* device already gone */ } catch (UnauthorizedAccessException) { }
            port.Dispose();
        }
        _reader?.Join(TimeSpan.FromSeconds(2));
        _reader = null;
        _incoming.Writer.TryComplete();
    }

    private SerialPort Port => _port ?? throw new IOException($"{Name} is not open.");

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var port = Port;
        try
        {
            // Synchronous write off the caller's thread: honours WriteTimeout (flow control hold-off).
            await Task.Run(() => port.Write(data.ToArray(), 0, data.Length), ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new IOException($"Write to {Name} timed out (flow control held off for 30 s).", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new IOException($"{Name} was closed.", ex);
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_chunkPos >= _chunk.Length)
        {
            try
            {
                _chunk = await _incoming.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException ex)
            {
                throw ex.InnerException as IOException ?? new IOException($"{Name} was closed.", ex);
            }
            _chunkPos = 0;
        }
        int n = Math.Min(buffer.Length, _chunk.Length - _chunkPos);
        _chunk.AsMemory(_chunkPos, n).CopyTo(buffer);
        _chunkPos += n;
        return n;
    }

    public void DiscardInput()
    {
        try { _port?.DiscardInBuffer(); } catch (InvalidOperationException) { }
        while (_incoming.Reader.TryRead(out _)) { }
        _chunk = [];
        _chunkPos = 0;
    }

    public async Task PulseDtrAsync(CancellationToken ct)
    {
        Port.DtrEnable = false;
        await Task.Delay(600, ct).ConfigureAwait(false);
        Port.DtrEnable = true;
        await Task.Delay(300, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}
