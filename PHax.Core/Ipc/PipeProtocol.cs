using System.Text.Json;
using System.Text.Json.Serialization;
using PHax.Core.Models;

namespace PHax.Core.Ipc;

/// <summary>
/// UI-to-service protocol: newline-delimited JSON envelopes over the named pipe <see cref="PipeName"/>.
/// The client sends requests; the service answers with a response carrying the same Id and pushes events at any time.
/// </summary>
public static class PipeProtocol
{
    public const string PipeName = "PHax.Service.v1";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(PipeEnvelope envelope) => JsonSerializer.Serialize(envelope, Json);
    public static PipeEnvelope? Deserialize(string line) => JsonSerializer.Deserialize<PipeEnvelope>(line, Json);
    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    public static T? FromElement<T>(JsonElement? element) =>
        element is { } e && e.ValueKind != JsonValueKind.Undefined ? e.Deserialize<T>(Json) : default;
}

public enum EnvelopeKind { Request, Response, Event }

public sealed class PipeEnvelope
{
    public EnvelopeKind Kind { get; set; }
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public JsonElement? Payload { get; set; }
    public string? Error { get; set; }
}

public static class PipeCommands
{
    public const string Ping = "ping";
    public const string GetStatus = "getStatus";
    /// <summary>Payload: <see cref="JobRef"/>. The job row and its document already exist in the database.</summary>
    public const string JobSubmitted = "jobSubmitted";
    public const string CancelJob = "cancelJob";
    public const string PauseJob = "pauseJob";
    public const string ResumeJob = "resumeJob";
    public const string RetryJob = "retryJob";
    public const string AnswerCall = "answerCall";
    public const string ReloadSettings = "reloadSettings";
    /// <summary>Payload: <see cref="ModemProbeRequest"/>. Service temporarily releases the active modem if the port matches.</summary>
    public const string ProbeModem = "probeModem";
    public const string ResetModem = "resetModem";
    public const string GetDiagnostics = "getDiagnostics";
    public const string ListPorts = "listPorts";
    /// <summary>Printers visible to the service (it runs as Local System, so per-user printers are not included).</summary>
    public const string ListPrinters = "listPrinters";
    public const string RunBackup = "runBackup";
    /// <summary>Payload: <see cref="RestoreRequest"/>.</summary>
    public const string RestoreBackup = "restoreBackup";
}

public static class PipeEvents
{
    public const string Status = "status";
    public const string JobProgress = "jobProgress";
    public const string JobChanged = "jobChanged";
    public const string IncomingCall = "incomingCall";
    public const string ReceptionProgress = "receptionProgress";
    public const string FaxReceived = "faxReceived";
    public const string Notification = "notification";
    public const string ProtocolEvent = "protocolEvent";
}

public sealed record JobRef(Guid JobId);

public sealed record RestoreRequest(string BackupPath);

public sealed record ModemProbeRequest(string PortName, int BaudRate, bool HardwareFlowControl, string ProfileId);

public sealed record ModemProbeResult(
    bool Opened,
    bool Responds,
    bool SupportsClass1,
    string? Identity,
    IReadOnlyList<string> FaxClasses,
    IReadOnlyList<int> TransmitModulations,
    IReadOnlyList<int> ReceiveModulations,
    string? Error,
    IReadOnlyList<string> Transcript);

public sealed record ServiceStatusDto
{
    public ModemState ModemState { get; init; }
    public string? PortName { get; init; }
    public string? ModemIdentity { get; init; }
    public string? FaxClass { get; init; }
    public T30Phase Phase { get; init; }
    public string? Modulation { get; init; }
    public FaxResolution? Resolution { get; init; }
    public Guid? ActiveJobId { get; init; }
    public bool IsSimulated { get; init; }
    public string? LastError { get; init; }
    public CallInfoDto? Call { get; init; }
    public DateTime Utc { get; init; } = DateTime.UtcNow;
}

public sealed record CallInfoDto
{
    public string State { get; init; } = "";
    public int RingCount { get; init; }
    public string? CallerNumber { get; init; }
    public string? CallerName { get; init; }
    public string? RemoteId { get; init; }
    public int PagesReceived { get; init; }
    public long BytesReceived { get; init; }
    public DateTime StartedUtc { get; init; }
    public bool AwaitingManualAnswer { get; init; }
}

public sealed record JobProgressDto(
    Guid JobId,
    OutboundStatus Status,
    T30Phase Phase,
    string Message,
    int Page,
    int TotalPages,
    double Percent);

public sealed record NotificationDto(NotificationKind Kind, string Title, string Message, Guid? RelatedId);

public sealed record DiagnosticsSnapshot(
    ServiceStatusDto Status,
    IReadOnlyList<DiagnosticEntryDto> AtHistory,
    IReadOnlyList<DiagnosticEntryDto> ProtocolEvents);

public sealed record DiagnosticEntryDto(DateTime Utc, string Category, string Message);
