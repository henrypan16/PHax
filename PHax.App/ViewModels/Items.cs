using CommunityToolkit.Mvvm.ComponentModel;
using PHax.Core.Ipc;
using PHax.Core.Models;
using Microsoft.UI.Text;
using Windows.UI.Text;

namespace PHax.App.ViewModels;

public sealed partial class OutboundItem : ObservableObject
{
    public OutboundItem(OutboundFax model) => Model = model;

    public OutboundFax Model { get; private set; }
    public Guid Id => Model.Id;

    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string LiveMessage { get; set; } = "";

    public string Title => string.IsNullOrWhiteSpace(Model.RecipientName) ? Model.FaxNumber : Model.RecipientName;
    public string Number => Model.FaxNumber;
    public string Subject => string.IsNullOrWhiteSpace(Model.Subject) ? "(no subject)" : Model.Subject;
    public bool IsActive => Model.Status is OutboundStatus.Dialing or OutboundStatus.Transmitting;
    public string StatusText => (Model.Status switch
    {
        OutboundStatus.RetryWaiting => "Retry scheduled",
        OutboundStatus.Uncertain => "Unconfirmed",
        _ => Model.Status.ToString(),
    }) + (Model.IsSimulated ? " · SIMULATED" : "");
    public string PagesText => Model.PageCount > 0 ? $"{Model.PageCount} page(s)" : $"{Model.Attachments.Count} attachment(s)";
    public string AttemptsText => Model.Attempts == 0 ? "Not attempted" : $"Attempt {Model.Attempts} of {Model.MaxAttempts}";
    public string NextRetryText => Model.NextAttemptUtc is { } t && Model.Status == OutboundStatus.RetryWaiting ? $"Next attempt {t.ToLocalTime():g}" : "";
    public string ErrorText => Model.LastError ?? "";
    public bool HasError => !string.IsNullOrEmpty(Model.LastError);
    public string CreatedText => Model.CreatedUtc.ToLocalTime().ToString("g");
    public string SentText => Model.SentUtc?.ToLocalTime().ToString("g") ?? "";
    public string DurationText => Model.DurationSeconds is { } d ? TimeSpan.FromSeconds(d).ToString(@"m\:ss") : "";
    public string ConfirmationText => Model.ConfirmationDetail ?? "";

    public void Update(OutboundFax model)
    {
        Model = model;
        if (!IsActive) { Progress = 0; LiveMessage = ""; }
        OnPropertyChanged(string.Empty);
    }

    public void Apply(JobProgressDto p)
    {
        Progress = p.Percent;
        LiveMessage = p.Message;
    }
}

public sealed partial class ReceivedItem : ObservableObject
{
    public ReceivedItem(ReceivedFax model) => Model = model;

    public ReceivedFax Model { get; private set; }
    public Guid Id => Model.Id;

    public string Title => Model.DisplayName;
    public string From => string.Join(" · ", new[] { Model.CallerName, Model.CallerId, Model.RemoteId }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
    public string ReceivedText => Model.ReceivedUtc.ToLocalTime().ToString("g");
    public string PagesText => $"{Model.PageCount} page(s)";
    public bool IsUnread => !Model.IsRead;
    public FontWeight Weight => Model.IsRead ? FontWeights.Normal : FontWeights.SemiBold;
    public string StatusText => Model.Status + (Model.IsSimulated ? " · SIMULATED" : "");
    public string ErrorText => Model.ErrorMessage ?? "";
    public bool HasDocument => Model.TiffPath is not null;

    public void Update(ReceivedFax model)
    {
        Model = model;
        OnPropertyChanged(string.Empty);
    }
}

public sealed class DiagnosticLine(string time, string category, string message)
{
    public string Time { get; } = time;
    public string Category { get; } = category;
    public string Message { get; } = message;

    public static DiagnosticLine From(DiagnosticEntryDto e) => new(e.Utc.ToLocalTime().ToString("HH:mm:ss.fff"), e.Category, e.Message);
}
