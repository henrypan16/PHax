using PHax.App.ViewModels;
using PHax.Core.Integrations;
using PHax.Core.Models;
using PHax.Data;

namespace PHax.App.Services;

/// <summary>What the app offers to integrations (<see cref="IntegrationLoader"/>): queuing faxes and showing notifications.</summary>
public sealed class IntegrationHost(FaxDocumentService documents, SettingsStore settingsStore, AppNotifications notifications) : IFaxSubmitter, IUserNotifier
{
    public async Task<OutboundFax> QueueAsync(FaxSubmission fax)
    {
        var job = await documents.SaveAsync(ToCompose(fax), queue: true);
        OutboxChanged();
        return job;
    }

    public async Task<OutboundFax> RecordFailedAsync(FaxSubmission fax, string error)
    {
        var job = await documents.CreateFailedAsync(ToCompose(fax), error);
        OutboxChanged();
        return job;
    }

    public void Show(string title, string message)
    {
        // Integrations call this from background threads; the sound setting is read fresh so changes apply at once.
        _ = Task.Run(async () => notifications.Show(title, message, (await settingsStore.LoadAsync()).Notifications.PlaySound));
    }

    private static ComposeData ToCompose(FaxSubmission fax) => new()
    {
        RecipientName = fax.RecipientName,
        FaxNumber = fax.FaxNumber,
        Subject = fax.Subject,
        IncludeCoverPage = fax.IncludeCoverPage,
        CoverMessage = fax.CoverMessage,
        Attachments = fax.Documents.Select(d => new ComposeAttachment { FileName = Path.GetFileName(d), Path = d }).ToList(),
        CoverFrom = fax.CoverFrom,
        CoverFromFax = fax.CoverFromFax,
        CoverFromPhone = fax.CoverFromPhone,
        CoverFromAddress = fax.CoverFromAddress,
        CoverToAddress = fax.CoverToAddress,
    };

    private static void OutboxChanged() => UiThread.Post(() => App.GetService<ShellViewModel>().RaiseOutboundChanged());
}
