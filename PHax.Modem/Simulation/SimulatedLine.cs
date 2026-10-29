namespace PHax.Modem.Simulation;

public enum SignalKind { Hdlc, Data, HangUp }

/// <summary>Something one simulated modem puts on the line for the other.</summary>
public sealed record LineSignal(SignalKind Kind, int Modulation, byte[] Payload, bool Corrupt = false);

public enum FaultAction { Deliver, Drop, Corrupt }

public enum SimulatedDialResult { Answer, Busy, NoDialTone, NoAnswer }

/// <param name="Sender">Name of the modem that transmitted.</param>
/// <param name="Fcf">FCF octet with the X bit cleared.</param>
/// <param name="Occurrence">1-based count of frames with this FCF sent by <paramref name="Sender"/> in this call.</param>
public sealed record HdlcFaultContext(string Sender, byte Fcf, int Occurrence);

public sealed record DataFaultContext(string Sender, int Modulation, byte[] Data, int Occurrence);

/// <summary>Fault injection for simulation tests.</summary>
public sealed class LineFaults
{
    public SimulatedDialResult DialResult { get; set; } = SimulatedDialResult.Answer;
    public Func<HdlcFaultContext, FaultAction>? OnHdlc { get; set; }
    /// <summary>May return modified data (e.g. noise) or null to drop the carrier entirely.</summary>
    public Func<DataFaultContext, byte[]?>? OnData { get; set; }
    /// <summary>Lowest bit rate at which TCF and page data arrive intact. Faster carriers are corrupted.</summary>
    public int MaxCleanBitRate { get; set; } = int.MaxValue;
}

/// <summary>An in-memory telephone exchange connecting simulated Class 1 modems.</summary>
public sealed class SimulatedLine
{
    private readonly Dictionary<string, SimulatedModemTransport> _numbers = new();
    private readonly object _lock = new();

    public LineFaults Faults { get; } = new();

    /// <summary>Endpoint that answers numbers that are not registered (used by the service's loopback simulator).</summary>
    public SimulatedModemTransport? DefaultEndpoint { get; set; }

    public void Register(string number, SimulatedModemTransport modem)
    {
        lock (_lock) _numbers[number] = modem;
    }

    internal SimulatedModemTransport? Lookup(string number)
    {
        lock (_lock) return _numbers.GetValueOrDefault(number) ?? DefaultEndpoint;
    }
}
