using System.ComponentModel;
using PHax.App.ViewModels;
using PHax.App.Views;
using PHax.Core.Ipc;
using PHax.Core.Models;
using PHax.Data;
using Microsoft.EntityFrameworkCore;

namespace PHax.App.Services;

/// <summary>
/// Opens the transmission popup when the service starts sending or receiving, keeps it updated from status and
/// job-progress events, shows the outcome briefly and closes it.
/// </summary>
public sealed class TransmissionPopupController(ShellViewModel shell, SettingsStore settingsStore, IDbContextFactory<FaxDbContext> dbFactory)
{
    private TransmissionPopup? _popup;
    private bool _active, _receiving, _dismissed, _enabled = true;
    private Guid? _jobId;
    private string _title = "";
    private DateTime _startedUtc;

    public void Start()
    {
        shell.PropertyChanged += OnShellChanged;
        shell.JobProgress += OnJobProgress;
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Status) && shell.Status is { } status) _ = OnStatusAsync(status);
    }

    private async Task OnStatusAsync(ServiceStatusDto status)
    {
        bool active = status.ModemState is ModemState.Sending or ModemState.Receiving;
        if (active && !_active)
        {
            _active = true;
            _dismissed = false;
            _receiving = status.ModemState == ModemState.Receiving;
            _jobId = status.ActiveJobId;
            _startedUtc = DateTime.UtcNow;
            _enabled = (await settingsStore.LoadAsync()).Notifications.ShowProgressPopup;
            _title = _receiving
                ? $"Receiving fax{From(status.Call)}"
                : $"Sending fax{await RecipientAsync(status.ActiveJobId)}";
            Show();
        }
        else if (!active && _active)
        {
            _active = false;
            await FinishAsync();
            return;
        }

        if (!_active || _popup is null) return;
        if (_receiving)
        {
            var call = status.Call;
            if (call?.RemoteId is { Length: > 0 } && !_title.Contains(call.RemoteId)) _title = $"Receiving fax from {call.RemoteId}";
            var parts = new List<string> { call?.State ?? status.Phase.ToString() };
            if (call is { PagesReceived: > 0 }) parts.Add($"{call.PagesReceived} page(s) received");
            if (status.Modulation is not null) parts.Add(status.Modulation);
            _popup.Update(_title, string.Join(" · ", parts), null, receiving: true);
        }
        else if (status.Phase is T30Phase.A or T30Phase.B)
        {
            _popup.Update(_title, status.Phase == T30Phase.A ? "Dialing…" : $"Negotiating{(status.Modulation is null ? "" : $" · {status.Modulation}")}…", null, receiving: false);
        }
    }

    private void OnJobProgress(JobProgressDto p)
    {
        if (!_active || _receiving || _popup is null || (_jobId is not null && p.JobId != _jobId)) return;
        bool sendingPages = p.Phase is T30Phase.C or T30Phase.D;
        var detail = sendingPages && p.TotalPages > 0
            ? $"Page {p.Page} of {p.TotalPages} · {p.Percent:0}%{(shell.Status?.Modulation is { } m ? $" · {m}" : "")}"
            : p.Message;
        _popup.Update(_title, detail, sendingPages ? p.Percent : null, receiving: false);
    }

    private void Show()
    {
        if (!_enabled || _dismissed) return;
        if (_popup is null)
        {
            _popup = new TransmissionPopup();
            _popup.Closed += (_, _) => _popup = null;
            _popup.Dismissed += () => _dismissed = true;
        }
        _popup.Update(_title, _receiving ? "Answering…" : "Starting…", null, _receiving);
        _popup.ShowQuietly();
    }

    private async Task FinishAsync()
    {
        if (_popup is null) return;
        var elapsed = DateTime.UtcNow - _startedUtc;
        string title, detail;
        bool ok;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (_receiving)
            {
                var fax = await db.ReceivedFaxes.AsNoTracking().OrderByDescending(r => r.ReceivedUtc).FirstOrDefaultAsync();
                ok = fax?.Status == ReceivedStatus.Complete;
                title = ok ? "Fax received" : "Fax reception incomplete";
                detail = fax is null ? "" : ok ? $"{fax.PageCount} page(s) from {fax.RemoteId ?? fax.CallerId ?? "unknown sender"}" : fax.ErrorMessage ?? "";
            }
            else
            {
                var job = _jobId is { } id ? await db.OutboundFaxes.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id) : null;
                ok = job?.Status == OutboundStatus.Sent;
                title = job?.Status switch
                {
                    OutboundStatus.Sent => "Fax sent",
                    OutboundStatus.RetryWaiting => "Fax not sent — retry scheduled",
                    OutboundStatus.Uncertain => "Fax delivery unconfirmed",
                    OutboundStatus.Cancelled => "Fax cancelled",
                    _ => "Fax failed",
                };
                detail = ok ? $"{job!.PageCount} page(s) delivered in {elapsed:m\\:ss}" : job?.LastError ?? "";
            }
        }
        catch (Exception)
        {
            ok = false;
            title = "Transmission finished";
            detail = "";
        }
        _popup?.Finish(title, detail, ok, ok ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(8));
    }

    private async Task<string> RecipientAsync(Guid? jobId)
    {
        if (jobId is null) return "";
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var job = await db.OutboundFaxes.AsNoTracking().FirstOrDefaultAsync(f => f.Id == jobId);
            if (job is null) return "";
            return $" to {(string.IsNullOrWhiteSpace(job.RecipientName) ? job.FaxNumber : job.RecipientName)}";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string From(CallInfoDto? call) =>
        call?.CallerName is { Length: > 0 } n ? $" from {n}" : call?.CallerNumber is { Length: > 0 } c ? $" from {c}" : "";
}
