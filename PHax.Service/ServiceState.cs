using PHax.Core.Ipc;
using PHax.Core.Models;
using PHax.Core.Services;
using PHax.Data;

namespace PHax.Service;

/// <summary>Fan-out of events to connected UI clients.</summary>
public sealed class EventBus
{
    public event Action<PipeEnvelope>? Published;

    public void Publish<T>(string type, T payload) =>
        Published?.Invoke(new PipeEnvelope { Kind = EnvelopeKind.Event, Id = Guid.NewGuid(), Type = type, Payload = PipeProtocol.ToElement(payload) });
}

/// <summary>Current service/modem status; every change is broadcast.</summary>
public sealed class StatusHolder(EventBus bus)
{
    private readonly object _lock = new();
    private ServiceStatusDto _current = new() { ModemState = ModemState.NotConfigured };

    public ServiceStatusDto Current { get { lock (_lock) return _current; } }

    public void Update(Func<ServiceStatusDto, ServiceStatusDto> change)
    {
        ServiceStatusDto next;
        lock (_lock) next = _current = change(_current) with { Utc = DateTime.UtcNow };
        bus.Publish(PipeEvents.Status, next);
    }
}

/// <summary>Records notifications and pushes them to the UI (the service runs in session 0 and cannot show toasts itself).</summary>
public sealed class Notifier(EventBus bus, Microsoft.EntityFrameworkCore.IDbContextFactory<FaxDbContext> db, ILogger<Notifier> log)
{
    public async Task NotifyAsync(NotificationKind kind, string title, string message, Guid? related, AppSettings settings)
    {
        bool enabled = kind switch
        {
            NotificationKind.FaxReceived => settings.Notifications.OnReceived,
            NotificationKind.FaxSent => settings.Notifications.OnSent,
            NotificationKind.FaxFailed => settings.Notifications.OnFailed,
            NotificationKind.RetryScheduled => settings.Notifications.OnRetryScheduled,
            NotificationKind.ModemDisconnected => settings.Notifications.OnModemDisconnected,
            _ => true,
        };
        try
        {
            await using var ctx = await db.CreateDbContextAsync();
            ctx.Notifications.Add(new NotificationRecord { Kind = kind, Title = title, Message = message, RelatedId = related, Delivered = !enabled });
            await ctx.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not store notification");
        }
        if (enabled) bus.Publish(PipeEvents.Notification, new NotificationDto(kind, title, message, related));
    }
}

/// <summary>Shared protocol log: in-memory history for diagnostics, forwarded to the UI and to Serilog.</summary>
public sealed class ServiceProtocolLog
{
    public ProtocolLog Log { get; } = new(4000);

    public ServiceProtocolLog(EventBus bus, ILogger<ServiceProtocolLog> logger)
    {
        Log.EntryAdded += e =>
        {
            bus.Publish(PipeEvents.ProtocolEvent, new DiagnosticEntryDto(e.Utc, e.Category, e.Message));
            if (e.Category is "T30" or "MODEM") logger.LogDebug("[{Category}] {Message}", e.Category, e.Message);
        };
    }
}
