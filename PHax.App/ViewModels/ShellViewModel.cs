using CommunityToolkit.Mvvm.ComponentModel;
using PHax.App.Services;
using PHax.Core.Ipc;
using PHax.Core.Models;
using PHax.Data;
using Microsoft.EntityFrameworkCore;

namespace PHax.App.ViewModels;

/// <summary>Service connection, modem status and app-wide change notifications.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ServiceClient _client;
    private readonly AppNotifications _notifications;
    private readonly SettingsStore _settings;
    private readonly IDbContextFactory<FaxDbContext> _db;

    [ObservableProperty] public partial bool ServiceConnected { get; set; }
    [ObservableProperty] public partial bool ServiceMissing { get; set; } = true;
    [ObservableProperty] public partial bool IsSimulated { get; set; }
    [ObservableProperty] public partial string ModemText { get; set; } = "Connecting to the PHax service…";
    [ObservableProperty] public partial bool ModemProblem { get; set; }
    [ObservableProperty] public partial string ModemProblemText { get; set; } = "";
    [ObservableProperty] public partial int UnreadCount { get; set; }
    [ObservableProperty] public partial ServiceStatusDto? Status { get; set; }

    public event Action? OutboundChanged;
    public event Action? InboundChanged;
    public event Action<JobProgressDto>? JobProgress;
    public event Action<DiagnosticEntryDto>? ProtocolEvent;

    public ShellViewModel(ServiceClient client, AppNotifications notifications, SettingsStore settings, IDbContextFactory<FaxDbContext> db)
    {
        _client = client;
        _notifications = notifications;
        _settings = settings;
        _db = db;
        client.ConnectionChanged += connected => UiThread.Post(async () =>
        {
            ServiceConnected = connected;
            ServiceMissing = !connected;
            if (connected)
            {
                try { Apply(await _client.RequestAsync<ServiceStatusDto>(PipeCommands.GetStatus)); } catch (Exception) { }
            }
            else
            {
                ModemText = "PHax service not running";
                IsSimulated = false;
            }
        });
        client.EventReceived += env => UiThread.Post(() => OnEvent(env));
    }

    private async void OnEvent(PipeEnvelope env)
    {
        switch (env.Type)
        {
            case PipeEvents.Status:
                Apply(PipeProtocol.FromElement<ServiceStatusDto>(env.Payload));
                break;
            case PipeEvents.JobChanged:
                OutboundChanged?.Invoke();
                break;
            case PipeEvents.JobProgress when PipeProtocol.FromElement<JobProgressDto>(env.Payload) is { } p:
                JobProgress?.Invoke(p);
                break;
            case PipeEvents.FaxReceived:
                InboundChanged?.Invoke();
                await RefreshUnreadAsync();
                break;
            case PipeEvents.ProtocolEvent when PipeProtocol.FromElement<DiagnosticEntryDto>(env.Payload) is { } d:
                ProtocolEvent?.Invoke(d);
                break;
            case PipeEvents.Notification when PipeProtocol.FromElement<NotificationDto>(env.Payload) is { } n:
                var s = await _settings.LoadAsync();
                _notifications.Show(n.Title, n.Message, s.Notifications.PlaySound);
                break;
        }
    }

    private void Apply(ServiceStatusDto? status)
    {
        if (status is null) return;
        Status = status;
        IsSimulated = status.IsSimulated;
        ModemText = status.ModemState switch
        {
            ModemState.Ready => $"Ready · {status.ModemIdentity} · {status.PortName} · Class {status.FaxClass}",
            ModemState.Sending => $"Sending · {status.Phase} · {status.Modulation}",
            ModemState.Receiving => $"Receiving · {status.Phase} · {status.Modulation}",
            ModemState.Ringing => $"Incoming call · ring {status.Call?.RingCount}",
            ModemState.NotConfigured => "No modem configured",
            ModemState.Unsupported => "Unsupported modem",
            ModemState.Disconnected => $"Modem disconnected ({status.PortName})",
            _ => status.ModemState.ToString(),
        };
        ModemProblem = status.ModemState is ModemState.Unsupported or ModemState.Disconnected or ModemState.NotConfigured or ModemState.Error;
        ModemProblemText = status.ModemState == ModemState.Unsupported
            ? $"Unsupported modem: {status.LastError} PHax requires a Fax Class 1 or 1.0 modem and does not fall back to Class 2."
            : status.LastError ?? "";
    }

    public async Task RefreshUnreadAsync()
    {
        try
        {
            await using var db = await _db.CreateDbContextAsync();
            UnreadCount = await db.ReceivedFaxes.CountAsync(r => r.Status == ReceivedStatus.Complete && !r.IsRead);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Unread count query failed");
        }
    }

    public void RaiseOutboundChanged() => OutboundChanged?.Invoke();
    public void RaiseInboundChanged() => InboundChanged?.Invoke();
}
