namespace PHax.Core.Models;

/// <summary>An outbound fax. Drafts, outbox items and sent faxes are all rows of this type, distinguished by <see cref="Status"/>.</summary>
public class OutboundFax
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public OutboundStatus Status { get; set; } = OutboundStatus.Draft;

    public string RecipientName { get; set; } = "";
    public string FaxNumber { get; set; } = "";
    public string Subject { get; set; } = "";
    public bool IncludeCoverPage { get; set; }
    public string CoverMessage { get; set; } = "";

    /// <summary>Prepared fax document (multi-page T.6 TIFF, fine resolution) relative to the storage root.</summary>
    public string? DocumentPath { get; set; }
    public int PageCount { get; set; }

    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public DateTime? NextAttemptUtc { get; set; }
    public DateTime? QueuedUtc { get; set; }

    public int CurrentPage { get; set; }
    public int PagesConfirmed { get; set; }
    public string? LastError { get; set; }
    public FaxOutcome? LastOutcome { get; set; }

    public DateTime? SentUtc { get; set; }
    public double? DurationSeconds { get; set; }
    /// <summary>Remote station identifier (CSI) reported during the successful session.</summary>
    public string? RemoteId { get; set; }
    public string? ConfirmationDetail { get; set; }

    /// <summary>True when the job was processed by the simulated modem. Simulated faxes never reached a phone line.</summary>
    public bool IsSimulated { get; set; }

    public List<FaxAttachment> Attachments { get; set; } = [];
    public List<TransmissionAttempt> TransmissionAttempts { get; set; } = [];
}

public class FaxAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OutboundFaxId { get; set; }
    public int Order { get; set; }
    public string OriginalFileName { get; set; } = "";
    /// <summary>Copy of the original file, relative to the storage root. Originals are preserved unmodified.</summary>
    public string StoredPath { get; set; } = "";
    public string ContentType { get; set; } = "";
    public int PageCount { get; set; }
    public long SizeBytes { get; set; }
}

public class TransmissionAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OutboundFaxId { get; set; }
    public int AttemptNumber { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; set; }
    public FaxOutcome? Outcome { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
    public string? Modulation { get; set; }
    public FaxResolution? Resolution { get; set; }
    public int PagesSent { get; set; }
    public int PagesConfirmed { get; set; }
    public string? RemoteId { get; set; }
    public bool PageDataTransmitted { get; set; }
    public string? ProtocolLogPath { get; set; }
}

public class ReceivedFax
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    public ReceivedStatus Status { get; set; } = ReceivedStatus.Receiving;
    public string DisplayName { get; set; } = "";
    public string? CallerId { get; set; }
    public string? CallerName { get; set; }
    public string? RemoteId { get; set; }
    public int PageCount { get; set; }
    /// <summary>TIFF path relative to the storage root (or absolute when a custom receive folder is used).</summary>
    public string? TiffPath { get; set; }
    public string? PdfPath { get; set; }
    public bool IsRead { get; set; }
    public double? DurationSeconds { get; set; }
    public string? Modulation { get; set; }
    public FaxResolution? Resolution { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ProtocolLogPath { get; set; }
    public bool IsSimulated { get; set; }
}

public class Recipient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string FaxNumber { get; set; } = "";
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
}

public class ModemConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 115200;
    public bool HardwareFlowControl { get; set; } = true;
    public string ProfileId { get; set; } = "generic-class1";
    /// <summary>Extra initialization commands, one per line, sent after the profile's commands.</summary>
    public string InitCommands { get; set; } = "";
    public bool IsActive { get; set; }
    public string? LastIdentity { get; set; }
    public string? LastSupportedClasses { get; set; }
    public DateTime? LastVerifiedUtc { get; set; }
}

public class AppSettingRecord
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public class NotificationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public NotificationKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public Guid? RelatedId { get; set; }
    /// <summary>Set when the desktop app has displayed the notification.</summary>
    public bool Delivered { get; set; }
}

public class BackupRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Path { get; set; } = "";
    public bool Success { get; set; }
    public bool Scheduled { get; set; }
    public string? Message { get; set; }
    public long SizeBytes { get; set; }
}
