namespace PHax.Modem.Profiles;

/// <summary>
/// Modem-specific behaviour. Values marked "unverified" are taken from vendor documentation and common practice;
/// they have not been confirmed by real send/receive tests (see <see cref="HardwareVerified"/>).
/// </summary>
public sealed record ModemProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = "";

    public string ResetCommand { get; init; } = "ATZ";
    /// <summary>Commands sent after reset. Each is expected to return OK; failures are logged, not fatal.</summary>
    public IReadOnlyList<string> InitCommands { get; init; } = ["ATE0V1Q0", "ATS0=0"];
    public string HardwareFlowControlCommand { get; init; } = "AT&K3";
    public string NoFlowControlCommand { get; init; } = "AT&K0";
    public string? CallerIdCommand { get; init; }
    /// <summary>Command to set the dial timeout register (seconds to wait for carrier), or null.</summary>
    public string? DialTimeoutCommand { get; init; } = "ATS7=60";

    /// <summary>Prefer +FCLASS=1.0 over 1 when both are listed.</summary>
    public bool PreferClass10 { get; init; } = true;

    /// <summary>True when the modem passes the two FCS octets after HDLC frame data on +FRH (T.31 default).</summary>
    public bool HdlcReceiveIncludesFcs { get; init; } = true;
    /// <summary>Byte sent to abort +FRH/+FRM when no carrier arrives (T.31: any character; CAN is conventional).</summary>
    public byte AbortByte { get; init; } = 0x18;

    /// <summary>+FTS value (10 ms units) before TCF, after DCS. T.30 requires 75 ms ± 20.</summary>
    public int SilenceBeforeTcf { get; init; } = 8;
    /// <summary>+FTS value after page data, before the post-message command.</summary>
    public int SilenceAfterPageData { get; init; } = 8;
    /// <summary>Issue +FRS before transmitting a response after receiving a frame.</summary>
    public bool WaitForSilenceBeforeResponse { get; init; }
    public int ResponseSilence { get; init; } = 7;

    /// <summary>Use V.17 short training for pages after the first following a TCF (T.30 recommendation).</summary>
    public bool V17ShortTrainAfterFirstPage { get; init; } = true;
    /// <summary>Upper bound for this modem regardless of advertised capabilities.</summary>
    public int MaxBitRate { get; init; } = 14400;

    public int DialTimeoutSeconds { get; init; } = 90;
    public int AnswerTimeoutSeconds { get; init; } = 30;
    public int CommandTimeoutMs { get; init; } = 3000;

    public bool HardwareVerified { get; init; }
    public string CompatibilityNotes { get; init; } = "";
}

public static class ModemProfiles
{
    public static readonly ModemProfile GenericClass1 = new()
    {
        Id = "generic-class1",
        DisplayName = "Generic Class 1 / 1.0 modem",
        Description = "Conservative defaults for any EIA-578 / T.31 Class 1 modem.",
        CompatibilityNotes = "Uses T.31 defaults. Not hardware-verified.",
    };

    public static readonly ModemProfile StarTechCx93010 = new()
    {
        Id = "startech-cx93010",
        DisplayName = "StarTech USB fax modem (Conexant CX93010)",
        Description = "USB virtual COM port; Class 1 and Class 1.0; V.17/V.29/V.27ter; 14.4 kbps fax.",
        InitCommands = ["ATE0V1Q0X4", "ATS0=0"],
        HardwareFlowControlCommand = "AT&K3",
        CallerIdCommand = "AT+VCID=1",
        PreferClass10 = true,
        CompatibilityNotes =
            "Unverified. Conexant Class 1.0 per datasheet. Caller ID via +VCID requires line support. " +
            "If TCF fails consistently at 14400, cap the maximum speed at 9600.",
    };

    public static readonly ModemProfile UsRobotics5637 = new()
    {
        Id = "usr5637",
        DisplayName = "USRobotics USR5637 USB modem",
        Description = "USB; EIA-578 Class 1; V.17/V.29/V.27ter.",
        InitCommands = ["ATE0V1Q0X4", "ATS0=0"],
        // USRobotics uses &H1 (CTS transmit flow control) and &R2 (RTS receive flow control) rather than &K3.
        HardwareFlowControlCommand = "AT&H1&R2",
        NoFlowControlCommand = "AT&H0&R1",
        CallerIdCommand = "AT#CID=1",
        PreferClass10 = false,
        CompatibilityNotes = "Unverified. Uses +FCLASS=1. Hardware flow control via &H1&R2.",
    };

    public static IReadOnlyList<ModemProfile> All { get; } = [GenericClass1, StarTechCx93010, UsRobotics5637];

    public static ModemProfile Get(string? id) => All.FirstOrDefault(p => p.Id == id) ?? GenericClass1;

    /// <summary>Suggests a profile from identification strings.</summary>
    public static ModemProfile Suggest(string? identity)
    {
        var s = identity?.ToUpperInvariant() ?? "";
        if (s.Contains("CX930") || s.Contains("CONEXANT") || s.Contains("STARTECH")) return StarTechCx93010;
        if (s.Contains("U.S. ROBOTICS") || s.Contains("USROBOTICS") || s.Contains("USR5637") || s.Contains("5637")) return UsRobotics5637;
        return GenericClass1;
    }
}
