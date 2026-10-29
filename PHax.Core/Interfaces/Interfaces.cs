using PHax.Core.Models;

namespace PHax.Core.Interfaces;

/// <summary>Raw byte transport to a modem (USB virtual COM port or simulator).</summary>
public interface ISerialTransport : IAsyncDisposable
{
    string Name { get; }
    bool IsOpen { get; }
    bool IsSimulated { get; }
    Task OpenAsync(CancellationToken ct);
    void Close();
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    /// <summary>Reads at least one byte. Throws <see cref="IOException"/> when the device disappears.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);
    void DiscardInput();
    /// <summary>Drops then raises DTR, which most modems treat as an unconditional hang-up.</summary>
    Task PulseDtrAsync(CancellationToken ct);
}

/// <summary>Class 1 / 1.0 (EIA-578, ITU-T T.31) modem primitives. No Class 2 operations exist on this interface.</summary>
public interface IFaxModem : IAsyncDisposable
{
    ModemIdentity? Identity { get; }
    Class1Capabilities? Capabilities { get; }
    bool IsSimulated { get; }
    event EventHandler<ModemUnsolicitedEvent>? Unsolicited;
    event EventHandler<Exception>? Disconnected;

    /// <summary>Resets the modem, verifies Class 1 support and selects Class 1 or 1.0. Throws <c>UnsupportedModemException</c> otherwise.</summary>
    Task<Class1Capabilities> InitializeAsync(CancellationToken ct);
    Task<ModemIdentity> IdentifyAsync(CancellationToken ct);
    Task<DialResult> DialAsync(string number, bool tone, TimeSpan timeout, CancellationToken ct);
    Task<bool> AnswerAsync(TimeSpan timeout, CancellationToken ct);
    Task HangupAsync(CancellationToken ct);

    /// <summary>AT+FTH=3. Frames include address and control; the control field of the last frame must be 0x13.</summary>
    Task<bool> TransmitHdlcAsync(IReadOnlyList<byte[]> frames, CancellationToken ct);
    /// <summary>AT+FRH=3, aborted after <paramref name="timeout"/> if no V.21 carrier is detected.</summary>
    Task<HdlcReceiveResult> ReceiveHdlcAsync(TimeSpan timeout, CancellationToken ct);
    /// <summary>AT+FTM=mod; data is sent in Class 1 bit order (LSB first).</summary>
    Task<DataTransmitStatus> TransmitDataAsync(int modulation, ReadOnlyMemory<byte> data, IProgress<long>? progress, CancellationToken ct);
    /// <summary>AT+FRM=mod; returns data until carrier loss.</summary>
    Task<DataReceiveResult> ReceiveDataAsync(int modulation, TimeSpan carrierTimeout, TimeSpan maxDuration, CancellationToken ct);
    /// <summary>AT+FTS=n (n in 10 ms units).</summary>
    Task<bool> TransmitSilenceAsync(int tenMs, CancellationToken ct);
    /// <summary>AT+FRS=n (n in 10 ms units).</summary>
    Task<bool> WaitForSilenceAsync(int tenMs, CancellationToken ct);
    /// <summary>Sends a raw AT command and returns all response lines (diagnostics only).</summary>
    Task<IReadOnlyList<string>> RawCommandAsync(string command, TimeSpan timeout, CancellationToken ct);
    /// <summary>Waits for the next unsolicited line (RING, caller ID) while idle.</summary>
    Task<ModemUnsolicitedEvent?> WaitForUnsolicitedAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>A running T.30 session (send or receive).</summary>
public interface IT30Session
{
    T30Phase Phase { get; }
    event EventHandler<FaxProgress>? Progress;
}

public interface IFaxSender : IT30Session
{
    Task<FaxSendResult> SendAsync(FaxSendRequest request, CancellationToken ct);
}

public interface IFaxReceiver : IT30Session
{
    Task<FaxReceiveResult> ReceiveAsync(FaxReceiveRequest request, CancellationToken ct);
}

/// <summary>Creates T.30 sessions bound to a Class 1 modem.</summary>
public interface IClass1FaxEngine
{
    IFaxSender CreateSender(IFaxModem modem);
    IFaxReceiver CreateReceiver(IFaxModem modem);
}

/// <summary>T.4 encoding of bilevel pages for transmission.</summary>
public interface IFaxImageEncoder
{
    /// <summary>
    /// Encodes one page as a T.4 bitstream: EOL before each line, fill to <paramref name="minBitsPerLine"/>, RTC at the end.
    /// </summary>
    /// <param name="lsbFirst">Pack bits LSB-first (Class 1 modem order) instead of MSB-first.</param>
    byte[] EncodeT4(FaxBitmap page, FaxCompression compression, int minBitsPerLine, bool lsbFirst);
}

public sealed record T4DecodeResult(FaxBitmap Page, int GoodLines, int BadLines, int MaxConsecutiveBadLines, bool RtcFound);

public interface IFaxImageDecoder
{
    T4DecodeResult DecodeT4(ReadOnlySpan<byte> data, int width, FaxResolution resolution, FaxCompression compression, bool lsbFirst);
}

/// <summary>Persistent outbound job queue.</summary>
public interface IFaxJobQueue
{
    /// <summary>Called once at startup: resolves jobs left in Dialing/Transmitting by a crash.</summary>
    Task RecoverAsync(CancellationToken ct);
    Task<OutboundFax?> TakeNextDueAsync(DateTime utcNow, CancellationToken ct);
    Task<DateTime?> GetNextDueTimeAsync(CancellationToken ct);
}

/// <summary>Sink for protocol-level events (T.30 frames, phase changes, AT traffic summaries).</summary>
public interface IProtocolLog
{
    void Log(string category, string message);
}
