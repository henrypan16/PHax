using System.Text;
using System.Threading.Channels;
using PHax.Core.Interfaces;
using PHax.Modem.Class1;

namespace PHax.Modem.Simulation;

public sealed record SimulatedModemOptions
{
    public string Manufacturer { get; init; } = "PHax";
    public string Model { get; init; } = "Simulated Class 1 Modem";
    public string Revision { get; init; } = "1.0";
    public string FaxClasses { get; init; } = "0,1,1.0,8";
    public string Modulations { get; init; } = "24,48,72,73,74,96,97,98,121,122,145,146";
    public bool IncludeFcs { get; init; } = true;
    public TimeSpan RingInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan AnswerWait { get; init; } = TimeSpan.FromSeconds(20);
    public string? CallerNumber { get; init; }
    /// <summary>Multiplier for +FTS delays (0 = no delay).</summary>
    public double TimeScale { get; init; } = 0;
}

/// <summary>
/// A simulated T.31 Class 1 DCE. It speaks AT commands on the DTE side and exchanges abstract carriers
/// (V.21 HDLC frames, high-speed data) with another simulated modem through a <see cref="SimulatedLine"/>.
/// It is for development and tests only and never touches a real telephone line.
/// </summary>
public sealed class SimulatedModemTransport : ISerialTransport
{
    private readonly SimulatedLine _line;
    private readonly SimulatedModemOptions _opt;
    private readonly Channel<byte[]> _fromDte = Channel.CreateUnbounded<byte[]>();
    private Channel<byte[]> _toDte = Channel.CreateUnbounded<byte[]>();
    private readonly Queue<byte> _dteBytes = new();
    private readonly LinkedList<LineSignal> _signals = new();
    private readonly SemaphoreSlim _signalCount = new(0);
    private readonly Dictionary<byte, int> _hdlcCounts = new();
    private int _dataCount;
    private byte[] _pendingRead = [];
    private int _pendingPos;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _faxClass = "0";

    // Call state.
    private SimulatedModemTransport? _peer;
    private bool _ringing;
    private TaskCompletionSource<bool>? _answered;
    private bool _unplugged;

    public SimulatedModemTransport(SimulatedLine line, string name, SimulatedModemOptions? options = null)
    {
        _line = line;
        Name = name;
        _opt = options ?? new SimulatedModemOptions();
    }

    public string Name { get; }
    public bool IsOpen => _loop is not null && !_unplugged;
    public bool IsSimulated => true;
    public bool InCall => _peer is not null;

    public Task OpenAsync(CancellationToken ct)
    {
        if (_unplugged) throw new IOException($"{Name} is unplugged.");
        if (_loop is not null) return Task.CompletedTask;
        _toDte = Channel.CreateUnbounded<byte[]>();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public void Close()
    {
        _cts?.Cancel();
        _loop = null;
        EndCall(notifyPeer: true);
    }

    /// <summary>Simulates a USB disconnection: all further I/O fails.</summary>
    public void Unplug()
    {
        _unplugged = true;
        _toDte.Writer.TryComplete(new IOException($"{Name}: device removed."));
        _cts?.Cancel();
        EndCall(notifyPeer: true);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_unplugged) throw new IOException($"{Name}: device removed.");
        _fromDte.Writer.TryWrite(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_pendingPos >= _pendingRead.Length)
        {
            try
            {
                _pendingRead = await _toDte.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException ex)
            {
                throw ex.InnerException as IOException ?? new IOException($"{Name} closed.", ex);
            }
            _pendingPos = 0;
        }
        int n = Math.Min(buffer.Length, _pendingRead.Length - _pendingPos);
        _pendingRead.AsMemory(_pendingPos, n).CopyTo(buffer);
        _pendingPos += n;
        return n;
    }

    public void DiscardInput()
    {
        while (_toDte.Reader.TryRead(out _)) { }
        _pendingRead = [];
        _pendingPos = 0;
    }

    public Task PulseDtrAsync(CancellationToken ct)
    {
        EndCall(notifyPeer: true);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    // ---- DTE side helpers ----

    private void Send(string text) => _toDte.Writer.TryWrite(Encoding.ASCII.GetBytes(text));
    private void SendBytes(byte[] data) => _toDte.Writer.TryWrite(data);
    private void Result(string code) => Send($"\r\n{code}\r\n");

    private async Task<int> ReadDteByteAsync(CancellationToken ct)
    {
        while (_dteBytes.Count == 0)
        {
            var chunk = await _fromDte.Reader.ReadAsync(ct).ConfigureAwait(false);
            foreach (var b in chunk) _dteBytes.Enqueue(b);
        }
        return _dteBytes.Dequeue();
    }

    private bool DteByteAvailable()
    {
        while (_fromDte.Reader.TryRead(out var chunk)) foreach (var b in chunk) _dteBytes.Enqueue(b);
        return _dteBytes.Count > 0;
    }

    // ---- line side ----

    internal void Deliver(LineSignal signal)
    {
        lock (_signals) _signals.AddLast(signal);
        _signalCount.Release();
    }

    private void PushBack(LineSignal signal)
    {
        lock (_signals) _signals.AddFirst(signal);
        _signalCount.Release();
    }

    private LineSignal TakeSignal()
    {
        lock (_signals)
        {
            var s = _signals.First!.Value;
            _signals.RemoveFirst();
            return s;
        }
    }

    /// <summary>Waits for a line signal; returns null if the DTE sent a byte (abort) first.</summary>
    private async Task<LineSignal?> WaitSignalOrAbortAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await _signalCount.WaitAsync(20, ct).ConfigureAwait(false)) return TakeSignal();
            if (DteByteAvailable())
            {
                _dteBytes.Clear();
                return null;
            }
            if (DateTime.UtcNow > deadline) return new LineSignal(SignalKind.HangUp, 0, []);
        }
    }

    private void Transmit(LineSignal signal)
    {
        var peer = _peer;
        if (peer is null) return;
        var faults = _line.Faults;
        if (signal.Kind == SignalKind.Hdlc && signal.Payload.Length >= 3)
        {
            byte fcf = Class1Fcf(signal.Payload[2]);
            int occurrence = _hdlcCounts[fcf] = _hdlcCounts.GetValueOrDefault(fcf) + 1;
            var action = faults.OnHdlc?.Invoke(new HdlcFaultContext(Name, fcf, occurrence)) ?? FaultAction.Deliver;
            if (action == FaultAction.Drop) return;
            if (action == FaultAction.Corrupt) signal = signal with { Corrupt = true };
        }
        else if (signal.Kind == SignalKind.Data)
        {
            var data = signal.Payload;
            if (Class1Modem.ModulationBitRate(signal.Modulation) > faults.MaxCleanBitRate)
                data = Noise(data.Length);
            if (faults.OnData is { } onData)
            {
                var replaced = onData(new DataFaultContext(Name, signal.Modulation, data, ++_dataCount));
                if (replaced is null) return;
                data = replaced;
            }
            signal = signal with { Payload = data };
        }
        peer.Deliver(signal);
    }

    private static byte Class1Fcf(byte fcf) => fcf is 0x80 or 0x40 or 0x20 or 0x81 or 0x41 or 0x21 ? fcf : (byte)(fcf & 0xFE);

    private static byte[] Noise(int length)
    {
        var data = new byte[Math.Max(16, length)];
        Random.Shared.NextBytes(data);
        return data;
    }

    private void EndCall(bool notifyPeer)
    {
        var peer = _peer;
        _peer = null;
        _ringing = false;
        _answered?.TrySetResult(false);
        if (peer is not null && notifyPeer)
        {
            peer.Deliver(new LineSignal(SignalKind.HangUp, 0, []));
            peer.RemoteEnded();
        }
    }

    private void RemoteEnded()
    {
        _peer = null;
        _ringing = false;
    }

    private bool IsClass1 => _faxClass is "1" or "1.0";

    // ---- main loop ----

    private async Task RunAsync(CancellationToken ct)
    {
        var command = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int b = await ReadDteByteAsync(ct).ConfigureAwait(false);
                if (b == '\r')
                {
                    var text = command.ToString().Trim();
                    command.Clear();
                    if (text.Length > 0) await ExecuteAsync(text, ct).ConfigureAwait(false);
                }
                else if (b >= 0x20 && b < 0x7F) command.Append((char)b);
                else command.Clear();
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    private async Task ExecuteAsync(string text, CancellationToken ct)
    {
        var cmd = text.ToUpperInvariant();
        if (!cmd.StartsWith("AT", StringComparison.Ordinal)) return;
        var body = cmd[2..];

        if (body.Length == 0) { Result("OK"); return; }
        if (body is "Z" or "&F") { _faxClass = "0"; EndCall(true); Result("OK"); return; }
        if (body == "+FCLASS=?") { Send($"\r\n{_opt.FaxClasses}\r\n"); Result("OK"); return; }
        if (body == "+FCLASS?") { Send($"\r\n{_faxClass}\r\n"); Result("OK"); return; }
        if (body.StartsWith("+FCLASS=", StringComparison.Ordinal))
        {
            var cls = body[8..];
            if (_opt.FaxClasses.Split(',').Contains(cls)) { _faxClass = cls; Result("OK"); }
            else Result("ERROR");
            return;
        }
        if (body is "+FTM=?" or "+FRM=?")
        {
            if (!IsClass1) { Result("ERROR"); return; }
            Send($"\r\n{_opt.Modulations}\r\n");
            Result("OK");
            return;
        }
        if (body is "+FTH=?" or "+FRH=?")
        {
            if (!IsClass1) { Result("ERROR"); return; }
            Send("\r\n3\r\n");
            Result("OK");
            return;
        }
        if (body is "+GMI" or "+FMI?" or "I3") { Send($"\r\n{_opt.Manufacturer}\r\n"); Result("OK"); return; }
        if (body is "+GMM" or "+FMM?" or "I0") { Send($"\r\n{_opt.Model}\r\n"); Result("OK"); return; }
        if (body is "+GMR" or "+FMR?" or "I1") { Send($"\r\n{_opt.Revision}\r\n"); Result("OK"); return; }
        if (body.StartsWith('D')) { await DialAsync(body[1..], ct).ConfigureAwait(false); return; }
        if (body == "A") { await AnswerAsync(ct).ConfigureAwait(false); return; }
        if (body is "H" or "H0") { EndCall(true); Result("OK"); return; }
        if (body == "+FTH=3") { await TransmitHdlcAsync(ct).ConfigureAwait(false); return; }
        if (body == "+FRH=3") { await ReceiveHdlcAsync(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false); return; }
        if (body.StartsWith("+FTM=", StringComparison.Ordinal) && int.TryParse(body[5..], out var ftm)) { await TransmitDataAsync(ftm, ct).ConfigureAwait(false); return; }
        if (body.StartsWith("+FRM=", StringComparison.Ordinal) && int.TryParse(body[5..], out var frm)) { await ReceiveDataAsync(frm, ct).ConfigureAwait(false); return; }
        if (body.StartsWith("+FTS=", StringComparison.Ordinal) && int.TryParse(body[5..], out var fts))
        {
            if (_opt.TimeScale > 0) await Task.Delay(TimeSpan.FromMilliseconds(fts * 10 * _opt.TimeScale), ct).ConfigureAwait(false);
            Result("OK");
            return;
        }
        if (body.StartsWith("+FRS=", StringComparison.Ordinal)) { Result("OK"); return; }
        if (body.StartsWith("+F", StringComparison.Ordinal)) { Result("ERROR"); return; }
        // Basic configuration commands (E, V, Q, X, S-registers, &K, &H, &R, +VCID, #CID ...).
        Result("OK");
    }

    private async Task DialAsync(string dial, CancellationToken ct)
    {
        var number = new string(dial.Where(char.IsAsciiDigit).ToArray());
        switch (_line.Faults.DialResult)
        {
            case SimulatedDialResult.Busy: Result("BUSY"); return;
            case SimulatedDialResult.NoDialTone: Result("NO DIALTONE"); return;
            case SimulatedDialResult.NoAnswer: Result("NO ANSWER"); return;
        }
        var callee = _line.Lookup(number);
        if (callee is null || callee == this || callee.InCall || !callee.IsOpen)
        {
            Result(callee is { InCall: true } ? "BUSY" : "NO CARRIER");
            return;
        }

        _hdlcCounts.Clear();
        _dataCount = 0;
        lock (_signals) _signals.Clear();
        while (_signalCount.CurrentCount > 0) _signalCount.Wait(0);
        _peer = callee;
        var answered = callee.Ring(this, _opt.CallerNumber);
        var finished = await Task.WhenAny(answered, Task.Delay(_opt.AnswerWait, ct)).ConfigureAwait(false);
        if (finished != answered || !answered.Result)
        {
            callee.StopRinging();
            _peer = null;
            Result("NO CARRIER");
            return;
        }
        // Answered: the DCE is now in +FRH=3 state waiting for the answerer's V.21 flags.
        await ReceiveHdlcAsync(TimeSpan.FromSeconds(40), ct).ConfigureAwait(false);
    }

    internal Task<bool> Ring(SimulatedModemTransport caller, string? callerNumber)
    {
        _peer = caller;
        _ringing = true;
        _hdlcCounts.Clear();
        _dataCount = 0;
        lock (_signals) _signals.Clear();
        while (_signalCount.CurrentCount > 0) _signalCount.Wait(0);
        _answered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            int rings = 0;
            while (_ringing)
            {
                Result("RING");
                if (++rings == 1 && callerNumber is not null) Send($"\r\nNMBR = {callerNumber}\r\n");
                await Task.Delay(_opt.RingInterval).ConfigureAwait(false);
            }
        });
        return _answered.Task;
    }

    private void StopRinging()
    {
        _ringing = false;
        _peer = null;
        _answered?.TrySetResult(false);
    }

    private async Task AnswerAsync(CancellationToken ct)
    {
        if (!_ringing || _peer is null) { Result("NO CARRIER"); return; }
        _ringing = false;
        _answered?.TrySetResult(true);
        // After ATA the DCE sends CED and enters +FTH=3: the next DTE bytes are HDLC frame data.
        Result("CONNECT");
        await TransmitHdlcFramesAsync(ct).ConfigureAwait(false);
    }

    private async Task<byte[]?> ReadDteFrameAsync(CancellationToken ct)
    {
        var decoder = new DleDecoder();
        while (!decoder.Complete)
        {
            int b = await ReadDteByteAsync(ct).ConfigureAwait(false);
            decoder.Feed((byte)b);
        }
        return decoder.ToArray();
    }

    private async Task TransmitHdlcAsync(CancellationToken ct)
    {
        if (_peer is null) { Result("ERROR"); return; }
        Result("CONNECT");
        await TransmitHdlcFramesAsync(ct).ConfigureAwait(false);
    }

    private async Task TransmitHdlcFramesAsync(CancellationToken ct)
    {
        while (true)
        {
            var frame = await ReadDteFrameAsync(ct).ConfigureAwait(false);
            if (frame is null || frame.Length < 2) { Result("ERROR"); return; }
            Transmit(new LineSignal(SignalKind.Hdlc, 3, frame));
            bool final = (frame[1] & 0x10) != 0;
            if (final) { Result("OK"); return; }
            Result("CONNECT");
        }
    }

    private async Task ReceiveHdlcAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_peer is null && _signalCount.CurrentCount == 0) { Result("NO CARRIER"); return; }
        while (true)
        {
            var signal = await WaitSignalOrAbortAsync(timeout, ct).ConfigureAwait(false);
            if (signal is null) { Result("OK"); return; }
            switch (signal.Kind)
            {
                case SignalKind.HangUp:
                    Result("NO CARRIER");
                    return;
                case SignalKind.Data:
                    // A V.21 receiver does not detect high-speed carriers; the data is lost.
                    continue;
                case SignalKind.Hdlc:
                    var payload = _opt.IncludeFcs ? HdlcFcs.Append(signal.Payload) : signal.Payload;
                    if (signal.Corrupt) payload[^1] ^= 0x5A;
                    Result("CONNECT");
                    var escaped = Dle.Escape(payload);
                    SendBytes([.. escaped, Dle.DLE, Dle.ETX]);
                    Result(signal.Corrupt ? "ERROR" : "OK");
                    return;
            }
        }
    }

    private async Task TransmitDataAsync(int modulation, CancellationToken ct)
    {
        if (_peer is null) { Result("ERROR"); return; }
        Result("CONNECT");
        var data = await ReadDteFrameAsync(ct).ConfigureAwait(false) ?? [];
        Transmit(new LineSignal(SignalKind.Data, modulation, data));
        Result("OK");
    }

    private async Task ReceiveDataAsync(int modulation, CancellationToken ct)
    {
        var signal = await WaitSignalOrAbortAsync(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        if (signal is null) { Result("OK"); return; }
        switch (signal.Kind)
        {
            case SignalKind.HangUp:
                Result("NO CARRIER");
                return;
            case SignalKind.Hdlc:
                // V.21 detected instead of the requested carrier: the frame stays available for +FRH.
                PushBack(signal);
                Result("+FCERROR");
                return;
            case SignalKind.Data:
                bool compatible = Class1Modem.ModulationBitRate(signal.Modulation) == Class1Modem.ModulationBitRate(modulation)
                                  && Family(signal.Modulation) == Family(modulation);
                var payload = compatible ? signal.Payload : Noise(signal.Payload.Length / 2);
                Result("CONNECT");
                SendBytes([.. Dle.Escape(payload), Dle.DLE, Dle.ETX]);
                Result("NO CARRIER");
                return;
        }
    }

    private static int Family(int code) => code switch
    {
        24 or 48 => 27,
        72 or 96 => 29,
        _ => 17,
    };
}
