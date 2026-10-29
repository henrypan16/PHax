namespace PHax.Core.Models;

public enum DialResult { Connected, Busy, NoAnswer, NoDialTone, NoCarrier, Error }

public enum HdlcStatus { Ok, FcsError, Timeout, NoCarrier, WrongCarrier, Error }

public enum DataStatus { Ok, Timeout, WrongCarrier, NoCarrier, Error }

/// <summary>Result of +FTM: NotStarted means no CONNECT (nothing reached the line); Aborted means data may have been partially sent.</summary>
public enum DataTransmitStatus { Completed, NotStarted, Aborted }

/// <param name="Frame">Address, control, FCF and FIF octets, without FCS, in Class 1 (LSB-first) octet values.</param>
public sealed record HdlcReceiveResult(HdlcStatus Status, byte[] Frame)
{
    public static HdlcReceiveResult Failed(HdlcStatus status) => new(status, []);
    /// <summary>True when the control field has the final-frame bit (0x13).</summary>
    public bool IsFinal => Frame.Length >= 2 && (Frame[1] & 0x10) != 0;
}

public sealed record DataReceiveResult(DataStatus Status, byte[] Data);

public sealed record ModemIdentity(string Manufacturer, string Model, string Revision, string RawInfo)
{
    public override string ToString() => $"{Manufacturer} {Model} {Revision}".Trim();
}

/// <summary>Class 1 capabilities reported by the modem. Never inferred: only filled from +FCLASS=?, +FTM=? and +FRM=? responses.</summary>
public sealed record Class1Capabilities(
    IReadOnlyList<string> FaxClasses,
    IReadOnlyList<int> TransmitModulations,
    IReadOnlyList<int> ReceiveModulations,
    string SelectedClass)
{
    public bool SupportsClass1 => FaxClasses.Contains("1") || FaxClasses.Contains("1.0");
}

public sealed record ModemUnsolicitedEvent(string Kind, string Text, int RingCount = 0, string? CallerNumber = null, string? CallerName = null);

public sealed class FaxSendRequest
{
    public required string DialString { get; init; }
    /// <summary>Pages at fine resolution; reduced automatically if the receiver lacks fine support.</summary>
    public required IReadOnlyList<FaxBitmap> Pages { get; init; }
    public string LocalId { get; init; } = "";
    public int MaxBitRate { get; init; } = 14400;
    public bool AllowFine { get; init; } = true;
    public bool AllowMr { get; init; } = true;
    /// <summary>False limits the session to V.29/V.27ter (V.17 often fails on VoIP and digital phone lines).</summary>
    public bool AllowV17 { get; init; } = true;
    public bool ToneDialing { get; init; } = true;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);
}

public sealed class FaxSendResult
{
    public FaxOutcome Outcome { get; set; }
    public string Message { get; set; } = "";
    public string? ErrorCode { get; set; }
    public int PagesSent { get; set; }
    public int PagesConfirmed { get; set; }
    /// <summary>True once any page data was put on the line.</summary>
    public bool PageDataTransmitted { get; set; }
    /// <summary>True only if MCF (or RTP) was received in response to EOP for the last page.</summary>
    public bool DeliveryConfirmed { get; set; }
    public string? RemoteId { get; set; }
    public string? Modulation { get; set; }
    public FaxResolution? Resolution { get; set; }
    public TimeSpan Duration { get; set; }
}

public sealed class FaxReceiveRequest
{
    public string LocalId { get; init; } = "";
    public int MaxBitRate { get; init; } = 14400;
    public bool AllowFine { get; init; } = true;
    /// <summary>Advertise MR (2D) coding in DIS.</summary>
    public bool AllowMr { get; init; } = true;
    /// <summary>Advertise V.17 in DIS. False makes senders use V.29/V.27ter.</summary>
    public bool AllowV17 { get; init; } = true;
    /// <summary>Set when the modem was already answered (ATA issued by the caller of the engine).</summary>
    public bool AlreadyAnswered { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>Called for each page after it has been confirmed with MCF.</summary>
    public Action<FaxBitmap, int>? PageReceived { get; init; }
}

public sealed class FaxReceiveResult
{
    public FaxOutcome Outcome { get; set; }
    public string Message { get; set; } = "";
    public string? ErrorCode { get; set; }
    public List<FaxBitmap> Pages { get; } = [];
    /// <summary>True when the sender signalled EOP and the session ended normally.</summary>
    public bool Complete { get; set; }
    public string? RemoteId { get; set; }
    public string? Modulation { get; set; }
    public FaxResolution? Resolution { get; set; }
    public TimeSpan Duration { get; set; }
}

public sealed record FaxProgress(
    T30Phase Phase,
    string Message,
    int Page = 0,
    int TotalPages = 0,
    long Bytes = 0,
    long TotalBytes = 0,
    string? Modulation = null,
    FaxResolution? Resolution = null,
    string? RemoteId = null);
