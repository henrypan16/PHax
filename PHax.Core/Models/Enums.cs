namespace PHax.Core.Models;

/// <summary>Lifecycle of an outbound fax. Only <see cref="Sent"/> means T.30 confirmation (MCF to EOP) was received.</summary>
public enum OutboundStatus
{
    Draft = 0,
    Queued = 1,
    Dialing = 2,
    Transmitting = 3,
    /// <summary>Failed attempt; waiting for <c>NextAttemptUtc</c>.</summary>
    RetryWaiting = 4,
    Paused = 5,
    Sent = 6,
    Failed = 7,
    Cancelled = 8,
    /// <summary>
    /// Page data was transmitted but the final confirmation was not received.
    /// The receiver may have printed the fax, so it is never retried automatically.
    /// </summary>
    Uncertain = 9,
}

public enum ReceivedStatus
{
    Receiving = 0,
    Complete = 1,
    Partial = 2,
    Failed = 3,
}

public enum FaxResolution
{
    /// <summary>204 x 98 dpi (3.85 lines/mm).</summary>
    Standard = 0,
    /// <summary>204 x 196 dpi (7.7 lines/mm).</summary>
    Fine = 1,
}

public enum FaxCompression
{
    MH = 0,
    MR = 1,
}

/// <summary>Classification of a session outcome used by the retry policy.</summary>
public enum FaxOutcome
{
    Success = 0,
    Busy,
    NoAnswer,
    NoDialTone,
    /// <summary>Call was answered but not by a fax machine, or no DIS was received.</summary>
    NoFaxAnswer,
    TrainingFailed,
    /// <summary>Protocol failure before any page data was sent. Safe to retry.</summary>
    CommunicationFailure,
    /// <summary>Some pages confirmed, later page rejected or unconfirmed.</summary>
    PartialFailure,
    /// <summary>Page data sent but confirmation missing. Not safe to retry automatically.</summary>
    UnconfirmedDelivery,
    RemoteRejected,
    ModemError,
    ModemDisconnected,
    Cancelled,
    /// <summary>Non-recoverable (invalid number, unsupported modem, unreadable document).</summary>
    PermanentFailure,
}

public enum ModemState
{
    NotConfigured = 0,
    Disconnected,
    Opening,
    Initializing,
    Ready,
    Ringing,
    Sending,
    Receiving,
    Unsupported,
    Error,
}

public enum T30Phase
{
    Idle = 0,
    /// <summary>Call establishment.</summary>
    A,
    /// <summary>Pre-message procedure: identification, capability exchange, training.</summary>
    B,
    /// <summary>In-message procedure: page transmission.</summary>
    C,
    /// <summary>Post-message procedure: end-of-page signalling and confirmation.</summary>
    D,
    /// <summary>Call release.</summary>
    E,
}

public enum NotificationKind
{
    FaxReceived,
    FaxSent,
    FaxFailed,
    RetryScheduled,
    ModemDisconnected,
    Info,
}
