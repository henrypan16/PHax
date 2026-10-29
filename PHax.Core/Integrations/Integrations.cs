using Microsoft.Extensions.DependencyInjection;
using PHax.Core.Models;

namespace PHax.Core.Integrations;

/// <summary>
/// Entry point of an optional add-on, loaded by the desktop app at startup from <c>PHax.Integration.*.dll</c> next to
/// <c>PHax.exe</c>. Needs a public parameterless constructor. Builds without such a DLL are the standard edition.
/// </summary>
public interface IPhaxIntegration
{
    /// <summary>
    /// Registers the integration's services, at least one <see cref="IAppIntegration"/>. The app's own registrations
    /// (AppPaths, SettingsStore, the database factory, <see cref="IFaxSubmitter"/>, <see cref="IUserNotifier"/>) are available.
    /// </summary>
    void ConfigureServices(IServiceCollection services);
}

/// <summary>
/// A feature that runs inside the desktop app (the signed-in user's session) and gets its own section on the
/// Settings → Integrations tab.
/// </summary>
public interface IAppIntegration : IDisposable
{
    string Title { get; }
    string Description { get; }
    /// <summary>Label of the on/off check box.</summary>
    string EnabledLabel { get; }
    /// <summary>Key in <see cref="IntegrationSettings"/> for the on/off switch; on when absent. The integration checks it itself.</summary>
    string EnabledSetting { get; }
    /// <summary>One line shown under the check box.</summary>
    string Status { get; }

    /// <summary>Called once after the main window is up, and only if the database opened.</summary>
    void Start();
}

/// <summary>An outgoing fax created by an integration. Cover page fields left null use the values from Settings.</summary>
public sealed class FaxSubmission
{
    public string RecipientName { get; init; } = "";
    public string FaxNumber { get; init; } = "";
    public string Subject { get; init; } = "";
    public bool IncludeCoverPage { get; init; }
    public string CoverMessage { get; init; } = "";
    /// <summary>Files to send, in order (PDF, image or Word). They are copied, so the originals can be moved afterwards.</summary>
    public IReadOnlyList<string> Documents { get; init; } = [];

    public string? CoverFrom { get; init; }
    public string? CoverFromFax { get; init; }
    public string? CoverFromPhone { get; init; }
    public string? CoverFromAddress { get; init; }
    public string? CoverToAddress { get; init; }
}

/// <summary>Adds faxes to the Outbox the same way the Compose window does.</summary>
public interface IFaxSubmitter
{
    /// <summary>Converts the documents and queues the job. Throws with a user-readable message if it cannot be queued.</summary>
    Task<OutboundFax> QueueAsync(FaxSubmission fax);

    /// <summary>Records a fax that could not be prepared as a Failed Outbox entry showing <paramref name="error"/>. Nothing is dialed.</summary>
    Task<OutboundFax> RecordFailedAsync(FaxSubmission fax, string error);
}

/// <summary>Windows notifications from the desktop app (respects the sound setting).</summary>
public interface IUserNotifier
{
    void Show(string title, string message);
}
