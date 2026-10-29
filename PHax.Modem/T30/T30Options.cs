using PHax.Modem.Profiles;

namespace PHax.Modem.T30;

/// <summary>T.30 timers and policy knobs. Defaults follow T.30 section 5.4.3.</summary>
public sealed record T30Options
{
    /// <summary>T1: time to identify the remote station after answer/dial (35 ± 5 s).</summary>
    public TimeSpan T1 { get; init; } = TimeSpan.FromSeconds(35);
    /// <summary>T2: wait for a command (6 ± 1 s).</summary>
    public TimeSpan T2 { get; init; } = TimeSpan.FromSeconds(6);
    /// <summary>T4: wait for a response before repeating a command (3 s ± 15 %).</summary>
    public TimeSpan T4 { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Times a command is sent before giving up (T.30: 3).</summary>
    public int CommandAttempts { get; init; } = 3;
    /// <summary>Retransmissions of a page after RTN before the session fails.</summary>
    public int PageRetransmissions { get; init; } = 3;

    public double TcfSeconds { get; init; } = 1.5;
    /// <summary>Receiver accepts TCF if it contains a run of zero octets at least this long.</summary>
    public double TcfMinZeroRunSeconds { get; init; } = 0.75;

    /// <summary>Page is answered MCF if at most this fraction of lines had errors.</summary>
    public double McfMaxBadLineRatio { get; init; } = 0.02;
    /// <summary>Page is kept and answered RTP (retrain) up to this fraction; above it, RTN.</summary>
    public double RtpMaxBadLineRatio { get; init; } = 0.10;
    public int MaxConsecutiveBadLines { get; init; } = 32;

    public int SilenceBeforeTcf { get; init; } = 8;
    public int SilenceAfterPageData { get; init; } = 8;
    public bool WaitForSilenceBeforeResponse { get; init; }
    public int ResponseSilence { get; init; } = 7;
    public bool V17ShortTrainAfterFirstPage { get; init; } = true;
    public int MaxBitRate { get; init; } = 14400;
    public TimeSpan DialTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public static T30Options FromProfile(ModemProfile p) => new()
    {
        SilenceBeforeTcf = p.SilenceBeforeTcf,
        SilenceAfterPageData = p.SilenceAfterPageData,
        WaitForSilenceBeforeResponse = p.WaitForSilenceBeforeResponse,
        ResponseSilence = p.ResponseSilence,
        V17ShortTrainAfterFirstPage = p.V17ShortTrainAfterFirstPage,
        MaxBitRate = p.MaxBitRate,
        DialTimeout = TimeSpan.FromSeconds(p.DialTimeoutSeconds),
        AnswerTimeout = TimeSpan.FromSeconds(p.AnswerTimeoutSeconds),
    };
}
