using CommunityToolkit.Mvvm.ComponentModel;
using PHax.App.Services;
using PHax.Core.Ipc;
using PHax.Core.Models;
using PHax.Core.Remote;
using PHax.Data;

namespace PHax.App.ViewModels;

/// <summary>Service connection, modem status and app-wide change notifications.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IServiceConnection _client;
    private readonly AppNotifications _notifications;
    private readonly SettingsStore _settings;
    private readonly IFaxData _data;

    [ObservableProperty] public partial bool ServiceConnected { get; set; }
    [ObservableProperty] public partial bool ServiceMissing { get; set; } = true;
    [ObservableProperty] public partial bool IsSimulated { get; set; }
    [ObservableProperty] public partial string ModemText { get; set; } = "Connecting…";
    [ObservableProperty] public partial string ConnectionMessage { get; set; } = "";
    [ObservableProperty] public partial bool ModemProblem { get; set; }
    [ObservableProperty] public partial string ModemProblemText { get; set; } = "";
    [ObservableProperty] public partial int UnreadCount { get; set; }
    [ObservableProperty] public partial ServiceStatusDto? Status { get; set; }

    public event Action? OutboundChanged;
    public event Action? InboundChanged;
    public event Action<JobProgressDto>? JobProgress;
    public event Action<DiagnosticEntryDto>? ProtocolEvent;

    /// <summary>This PC uses another PC's modem. Decided at startup; changing it restarts the app.</summary>
    public bool IsClient { get; }
    public string ConnectionTitle => IsClient ? "Not connected to the fax server" : "PHax service not running";

    public ShellViewModel(IServiceConnection client, AppNotifications notifications, SettingsStore settings, IFaxData data, NetworkConfig network)
    {
        _client = client;
        _notifications = notifications;
        _settings = settings;
        _data = data;
        IsClient = network.Mode == NetworkMode.Client;
        ConnectionMessage = IsClient ? client.UnavailableMessage
            : "Faxes cannot be sent or received until the PHax Windows Service is running. Drafts can still be edited.";
        client.ConnectionChanged += connected => UiThread.Post(async () =>
        {
            ServiceConnected = connected;
            ServiceMissing = !connected;
            if (connected)
            {
                try { Apply(await _client.RequestAsync<ServiceStatusDto>(PipeCommands.GetStatus)); } catch (Exception) { }
                // Anything may have changed while this PC was away from the server.
                if (IsClient) { OutboundChanged?.Invoke(); InboundChanged?.Invoke(); }
            }
            else
            {
                ModemText = IsClient ? "Not connected to the fax server" : "PHax service not running";
                IsSimulated = false;
                if (IsClient) ConnectionMessage = client.UnavailableMessage;
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
            case PipeEvents.FaxReceived or PipeEvents.InboxChanged:
                InboundChanged?.Invoke();
                await RefreshUnreadAsync();
                break;
            case PipeEvents.ProtocolEvent when PipeProtocol.FromElement<DiagnosticEntryDto>(env.Payload) is { } d:
                ProtocolEvent?.Invoke(d);
                break;
            case PipeEvents.Notification when PipeProtocol.FromElement<NotificationDto>(env.Payload) is { } n:
                var s = await _settings.LoadAsync();
                // The service applied the server's choices; each client also has its own.
                if (!IsClient || Wanted(n.Kind, s.Notifications)) _notifications.Show(n.Title, n.Message, s.Notifications.PlaySound);
                break;
        }
    }

    private static bool Wanted(NotificationKind kind, NotificationSettings n) => kind switch
    {
        NotificationKind.FaxReceived => n.OnReceived,
        NotificationKind.FaxSent => n.OnSent,
        NotificationKind.FaxFailed => n.OnFailed,
        NotificationKind.RetryScheduled => n.OnRetryScheduled,
        NotificationKind.ModemDisconnected => n.OnModemDisconnected,
        _ => true,
    };

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
            UnreadCount = await _data.CountUnreadAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Unread count query failed");
        }
    }

    public void RaiseOutboundChanged() => OutboundChanged?.Invoke();
    public void RaiseInboundChanged() => InboundChanged?.Invoke();
}
