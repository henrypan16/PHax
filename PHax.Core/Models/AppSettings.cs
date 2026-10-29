namespace PHax.Core.Models;

/// <summary>All user settings. Persisted as a single JSON document in the AppSettings table.</summary>
public class AppSettings
{
    public GeneralSettings General { get; set; } = new();
    public IncomingSettings Incoming { get; set; } = new();
    public OutgoingSettings Outgoing { get; set; } = new();
    public RedialSettings Redial { get; set; } = new();
    public BackupSettings Backup { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public ModemSettings Modem { get; set; } = new();
}

public class GeneralSettings
{
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    /// <summary>Root for fax documents. Empty = default under ProgramData.</summary>
    public string StorageDirectory { get; set; } = "";
    public bool EnableLogging { get; set; } = true;
    /// <summary>Include page-data hex dumps in protocol logs. Off by default: documents may be confidential.</summary>
    public bool LogDocumentContent { get; set; }
}

public class IncomingSettings
{
    public bool AutoReceive { get; set; } = true;
    public int AnswerAfterRings { get; set; } = 2;
    public bool AutoSave { get; set; } = true;
    public bool AutoConvertToPdf { get; set; }
    /// <summary>Print each completed received fax (done by the service, so it works without the app running).</summary>
    public bool AutoPrint { get; set; }
    /// <summary>Printer for automatic printing; empty = the service's default printer.</summary>
    public string AutoPrintPrinter { get; set; } = "";
    /// <summary>Empty = default Inbox folder under the storage directory.</summary>
    public string ReceiveFolder { get; set; } = "";
    /// <summary>Local station identifier sent as CSI/TSI (max 20 chars: digits, space, '+').</summary>
    public string LocalStationId { get; set; } = "";
}

public class OutgoingSettings
{
    public bool EnableSending { get; set; } = true;
    public string DialPrefix { get; set; } = "";
    public bool DefaultCoverPage { get; set; }
    public string CoverPageFrom { get; set; } = "";
    public int TransmissionTimeoutSeconds { get; set; } = 900;
    /// <summary>Upper bound in bits/s: 14400, 12000, 9600, 7200, 4800, 2400.</summary>
    public int MaxSpeed { get; set; } = 14400;
    public bool UseFineResolution { get; set; } = true;
    public bool AllowMrCompression { get; set; } = true;
    /// <summary>Tone (T) or pulse (P) dialing.</summary>
    public bool ToneDialing { get; set; } = true;
}

public class RedialSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxAttempts { get; set; } = 3;
    public int DelayMinutes { get; set; } = 5;
    public bool RetryOnBusy { get; set; } = true;
    public bool RetryOnNoAnswer { get; set; } = true;
    public bool RetryOnCommunicationFailure { get; set; } = true;
}

public class BackupSettings
{
    public bool Enabled { get; set; }
    public string BackupDirectory { get; set; } = "";
    public bool IncludeInbox { get; set; } = true;
    public bool IncludeSent { get; set; } = true;
    public bool IncludeDrafts { get; set; } = true;
    public bool IncludeDatabase { get; set; } = true;
    public bool IncludeLogs { get; set; } = true;
    public int ScheduleIntervalHours { get; set; } = 24;
    public int KeepBackups { get; set; } = 10;
}

public class NotificationSettings
{
    public bool OnReceived { get; set; } = true;
    public bool OnSent { get; set; } = true;
    public bool OnFailed { get; set; } = true;
    public bool OnRetryScheduled { get; set; } = true;
    public bool OnModemDisconnected { get; set; } = true;
    public bool PlaySound { get; set; } = true;
    /// <summary>Show a small always-on-top progress window at the top of the screen while a fax is sent or received.</summary>
    public bool ShowProgressPopup { get; set; } = true;
}

public class ModemSettings
{
    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 115200;
    public bool HardwareFlowControl { get; set; } = true;
    public string ProfileId { get; set; } = "generic-class1";
    public string ExtraInitCommands { get; set; } = "";
    /// <summary>Prefer +FCLASS=1.0 when the modem lists it.</summary>
    public bool PreferClass10 { get; set; } = true;
    /// <summary>
    /// For VoIP / digital phone lines: never use V.17 or MR coding, in either direction (max V.29 9600, MH).
    /// Slower but far more reliable when the line passes through a VoIP gateway.
    /// </summary>
    public bool LineCompatibilityMode { get; set; }
    /// <summary>
    /// Development only: route all fax traffic to the built-in simulated modem and loopback endpoint.
    /// Faxes processed this way are flagged as simulated and never reach a telephone line.
    /// </summary>
    public bool UseSimulator { get; set; }
}
