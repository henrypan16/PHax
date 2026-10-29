using PHax.Core.Interfaces;
using PHax.Core.Models;

namespace PHax.Modem.T30;

public abstract class T30SessionBase : IT30Session
{
    protected readonly IFaxModem Modem;
    protected readonly IProtocolLog Log;
    protected readonly T30Options Options;

    protected T30SessionBase(IFaxModem modem, IProtocolLog log, T30Options options)
    {
        Modem = modem;
        Log = log;
        Options = options;
    }

    public T30Phase Phase { get; private set; } = T30Phase.Idle;
    public event EventHandler<FaxProgress>? Progress;

    protected string? CurrentModulation { get; set; }
    protected FaxResolution? CurrentResolution { get; set; }
    protected string? RemoteId { get; set; }

    protected void Report(T30Phase phase, string message, int page = 0, int total = 0, long bytes = 0, long totalBytes = 0)
    {
        if (phase != Phase) Log.Log("T30", $"Phase {phase}: {message}");
        Phase = phase;
        Progress?.Invoke(this, new FaxProgress(phase, message, page, total, bytes, totalBytes, CurrentModulation, CurrentResolution, RemoteId));
    }

    protected async Task<bool> SendFramesAsync(CancellationToken ct, params byte[][] frames)
    {
        foreach (var f in frames)
        {
            var parsed = T30Frame.TryParse(f);
            Log.Log("T30", $"TX {parsed}");
        }
        return await Modem.TransmitHdlcAsync(frames, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Receives one HDLC frame group (until a final frame) within <paramref name="timeout"/>.
    /// Frames with FCS errors are dropped; returns an empty list on timeout or carrier loss.
    /// </summary>
    protected async Task<List<T30Frame>> ReceiveGroupAsync(TimeSpan timeout, CancellationToken ct)
    {
        var frames = new List<T30Frame>();
        var deadline = DateTime.UtcNow + timeout;
        bool first = true;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return frames;
            // Subsequent frames of a group follow immediately; allow a few seconds for each.
            var wait = first ? remaining : TimeSpan.FromSeconds(Math.Min(remaining.TotalSeconds, 4));
            var r = await Modem.ReceiveHdlcAsync(wait, ct).ConfigureAwait(false);
            first = false;
            switch (r.Status)
            {
                case HdlcStatus.Ok:
                    var frame = T30Frame.TryParse(r.Frame);
                    if (frame is null)
                    {
                        Log.Log("T30", $"RX unparseable frame {Convert.ToHexString(r.Frame)}");
                        continue;
                    }
                    Log.Log("T30", $"RX {frame}");
                    frames.Add(frame);
                    if (frame.Final) return frames;
                    break;
                case HdlcStatus.FcsError:
                    Log.Log("T30", "RX frame with bad FCS (discarded)");
                    // Keep listening: the frame group may continue.
                    if (r.IsFinal) return frames;
                    break;
                case HdlcStatus.WrongCarrier:
                    Log.Log("T30", "RX: unexpected carrier (+FCERROR)");
                    if (frames.Count > 0) return frames;
                    break;
                default:
                    if (r.Status != HdlcStatus.Timeout) Log.Log("T30", $"RX: {r.Status}");
                    return frames;
            }
        }
    }

    protected async Task SendDcnAndHangupAsync(byte x)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await SendFramesAsync(cts.Token, T30Frame.Build((byte)(Fcf.DCN | x), [], final: true)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Log("T30", $"DCN not sent: {ex.Message}");
        }
        await HangupQuietlyAsync().ConfigureAwait(false);
    }

    protected async Task HangupQuietlyAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await Modem.HangupAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Log("T30", $"Hang-up failed: {ex.Message}");
        }
        Report(T30Phase.Idle, "Line released");
    }
}
