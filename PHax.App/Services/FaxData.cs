using PHax.Core.Ipc;
using PHax.Core.Models;
using PHax.Core.Remote;
using PHax.Core.Services;
using PHax.Data;
using PHax.Imaging.Encoding;
using Microsoft.EntityFrameworkCore;

namespace PHax.App.Services;

/// <summary>The connection the UI uses for live status and job commands: the local service, or the server on a client PC.</summary>
public interface IServiceConnection
{
    bool IsConnected { get; }
    /// <summary>What to tell the user when <see cref="IsConnected"/> is false.</summary>
    string UnavailableMessage { get; }
    event Action<bool>? ConnectionChanged;
    event Action<PipeEnvelope>? EventReceived;
    Task<T?> RequestAsync<T>(string type, object? payload = null, TimeSpan? timeout = null);
    Task<bool> TrySendAsync(string type, object? payload = null);
}

/// <summary>Cover page details that belong to the fax machine, wherever the fax is prepared.</summary>
public sealed record CoverDefaults(string From, string StationId, bool IncludeByDefault);

/// <summary>
/// Where faxes live, as the UI sees them: the database on this PC, or the server on a client PC. Drafts are not here
/// because they always stay on the PC where they are written.
/// </summary>
public interface IFaxData
{
    Task<List<OutboundFax>> ListOutboundAsync(OutboundView view, string? search);
    Task<OutboundFax?> GetOutboundAsync(Guid id);
    Task<IReadOnlyList<FaxBitmap>> LoadOutboundPagesAsync(OutboundFax fax, CancellationToken ct = default);
    Task ResendAsync(Guid id);
    Task DeleteOutboundAsync(Guid id);

    Task<List<ReceivedFax>> ListReceivedAsync(bool includeIncomplete, string? search);
    Task<ReceivedFax?> GetLatestReceivedAsync();
    Task<int> CountUnreadAsync();
    Task<IReadOnlyList<FaxBitmap>> LoadReceivedPagesAsync(ReceivedFax fax, CancellationToken ct = default);
    Task SetReadAsync(Guid id, bool read);
    Task RenameReceivedAsync(Guid id, string name);
    Task DeleteReceivedAsync(Guid id);

    Task<List<Recipient>> RecentRecipientsAsync();
    Task<CoverDefaults> GetCoverDefaultsAsync();
}

/// <summary>Server mode: faxes are in this PC's database.</summary>
public sealed class LocalFaxData(IDbContextFactory<FaxDbContext> dbFactory, AppPaths paths, SettingsStore settingsStore, FaxRecords records, IServiceConnection service) : IFaxData
{
    public async Task<List<OutboundFax>> ListOutboundAsync(OutboundView view, string? search)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.OutboundFaxes.AsNoTracking().Include(f => f.Attachments).AsQueryable();
        q = view == OutboundView.Sent ? q.Where(f => f.Status == OutboundStatus.Sent) : q.Where(f => f.Status != OutboundStatus.Draft && f.Status != OutboundStatus.Sent);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(f => f.RecipientName.Contains(s) || f.FaxNumber.Contains(s) || f.Subject.Contains(s));
        }
        return await q.OrderByDescending(f => f.UpdatedUtc).Take(500).ToListAsync();
    }

    public async Task<OutboundFax?> GetOutboundAsync(Guid id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.OutboundFaxes.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id);
    }

    public Task<IReadOnlyList<FaxBitmap>> LoadOutboundPagesAsync(OutboundFax fax, CancellationToken ct = default) => ReadAsync(fax.DocumentPath, ct);

    public async Task ResendAsync(Guid id)
    {
        var job = await records.ResendAsync(id);
        await service.TrySendAsync(PipeCommands.JobSubmitted, new JobRef(job.Id));
    }

    public Task DeleteOutboundAsync(Guid id) => records.DeleteOutboundAsync(id);

    public async Task<List<ReceivedFax>> ListReceivedAsync(bool includeIncomplete, string? search)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var q = db.ReceivedFaxes.AsNoTracking().AsQueryable();
        q = includeIncomplete ? q.Where(r => r.Status != ReceivedStatus.Receiving) : q.Where(r => r.Status == ReceivedStatus.Complete);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(r => r.DisplayName.Contains(s) || (r.CallerId ?? "").Contains(s) || (r.RemoteId ?? "").Contains(s) || (r.CallerName ?? "").Contains(s));
        }
        return await q.OrderByDescending(r => r.ReceivedUtc).Take(includeIncomplete ? 30 : 1000).ToListAsync();
    }

    public async Task<ReceivedFax?> GetLatestReceivedAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ReceivedFaxes.AsNoTracking().OrderByDescending(r => r.ReceivedUtc).FirstOrDefaultAsync();
    }

    public async Task<int> CountUnreadAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ReceivedFaxes.CountAsync(r => r.Status == ReceivedStatus.Complete && !r.IsRead);
    }

    public Task<IReadOnlyList<FaxBitmap>> LoadReceivedPagesAsync(ReceivedFax fax, CancellationToken ct = default) => ReadAsync(fax.TiffPath, ct);

    public async Task SetReadAsync(Guid id, bool read)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ReceivedFaxes.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsRead, read));
        await InboxChangedAsync();
    }

    public async Task RenameReceivedAsync(Guid id, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ReceivedFaxes.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.DisplayName, name));
        await InboxChangedAsync();
    }

    public async Task DeleteReceivedAsync(Guid id)
    {
        await records.DeleteReceivedAsync(id);
        await InboxChangedAsync();
    }

    public async Task<List<Recipient>> RecentRecipientsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Recipients.AsNoTracking().OrderByDescending(r => r.LastUsedUtc).Take(50).ToListAsync();
    }

    public async Task<CoverDefaults> GetCoverDefaultsAsync()
    {
        var s = await settingsStore.LoadAsync();
        return new CoverDefaults(s.Outgoing.CoverPageFrom, s.Incoming.LocalStationId, s.Outgoing.DefaultCoverPage);
    }

    /// <summary>Clients share this Inbox, so they hear about changes made on this PC too.</summary>
    private Task InboxChangedAsync() => service.TrySendAsync(PipeCommands.InboxChanged);

    private async Task<IReadOnlyList<FaxBitmap>> ReadAsync(string? storedPath, CancellationToken ct)
    {
        if (storedPath is null) return [];
        var path = paths.Resolve(storedPath);
        return await Task.Run(() => TiffFax.Read(path), ct);
    }
}

/// <summary>Client mode: faxes are on the server and page images are fetched when viewed, not kept on this PC.</summary>
public sealed class RemoteFaxData(RemoteConnection remote, IDbContextFactory<FaxDbContext> dbFactory, FaxRecords records) : IFaxData
{
    private CoverDefaults? _lastCover;

    private FaxServerClient Server => remote.Server;

    public async Task<List<OutboundFax>> ListOutboundAsync(OutboundView view, string? search)
    {
        var rows = await Server.ListOutboundAsync(view, search);
        if (view == OutboundView.Sent) return rows;
        // Integrations can record a fax that could not even be prepared; that record only exists on this PC.
        await using var db = await dbFactory.CreateDbContextAsync();
        rows.AddRange(await db.OutboundFaxes.AsNoTracking().Where(f => f.Status == OutboundStatus.Failed).ToListAsync());
        return rows.OrderByDescending(f => f.UpdatedUtc).ToList();
    }

    public Task<OutboundFax?> GetOutboundAsync(Guid id) => Server.GetOutboundAsync(id);

    public async Task<IReadOnlyList<FaxBitmap>> LoadOutboundPagesAsync(OutboundFax fax, CancellationToken ct = default)
    {
        if (fax.DocumentPath is null) return [];
        var bytes = await Server.GetOutboundDocumentAsync(fax.Id, ct);
        return await Task.Run(() => TiffFax.Read(bytes), ct);
    }

    public Task ResendAsync(Guid id) => Server.ResendAsync(id, Guid.NewGuid());

    public async Task DeleteOutboundAsync(Guid id)
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            if (await db.OutboundFaxes.AnyAsync(f => f.Id == id))
            {
                await records.DeleteOutboundAsync(id);
                return;
            }
        }
        await Server.DeleteOutboundAsync(id);
    }

    public Task<List<ReceivedFax>> ListReceivedAsync(bool includeIncomplete, string? search) => Server.ListReceivedAsync(includeIncomplete, search);

    public async Task<ReceivedFax?> GetLatestReceivedAsync() => (await Server.ListReceivedAsync(includeIncomplete: true, null)).FirstOrDefault();

    public Task<int> CountUnreadAsync() => Server.GetUnreadCountAsync();

    public async Task<IReadOnlyList<FaxBitmap>> LoadReceivedPagesAsync(ReceivedFax fax, CancellationToken ct = default)
    {
        if (fax.TiffPath is null) return [];
        var bytes = await Server.GetReceivedDocumentAsync(fax.Id, ct);
        return await Task.Run(() => TiffFax.Read(bytes), ct);
    }

    public Task SetReadAsync(Guid id, bool read) => Server.SetReadAsync(id, read);
    public Task RenameReceivedAsync(Guid id, string name) => Server.RenameReceivedAsync(id, name);
    public Task DeleteReceivedAsync(Guid id) => Server.DeleteReceivedAsync(id);

    public async Task<List<Recipient>> RecentRecipientsAsync()
    {
        try { return await Server.GetRecipientsAsync(); }
        catch (FaxServerException) { return []; } // Compose must still open while the server is away.
    }

    public async Task<CoverDefaults> GetCoverDefaultsAsync()
    {
        // Compose and drafts must keep working while the server is away; a fax cannot be sent until it is back anyway.
        try
        {
            if (await Server.GetConfigAsync() is { } c) _lastCover = new CoverDefaults(c.CoverPageFrom, c.StationId, c.DefaultCoverPage);
        }
        catch (FaxServerException) { }
        return _lastCover ?? new CoverDefaults("", "", false);
    }
}

/// <summary>
/// Client mode's <see cref="IServiceConnection"/>: follows the server's event stream, reconnects by itself, and
/// maps the job commands the pages use onto the server's API.
/// </summary>
public sealed class RemoteConnection : IServiceConnection, IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly NetworkConfig _config;
    private readonly CancellationTokenSource _cts = new();
    private readonly FaxServerClient? _server;
    private Task? _loop;

    public RemoteConnection(NetworkConfig config)
    {
        _config = config;
        if (ServerAddress.ToBaseUri(config.ServerAddress) is { } uri && config.IsPaired && NetworkConfigStore.UnprotectToken(config.ProtectedToken) is { } token)
            _server = new FaxServerClient(uri, config.ServerThumbprint, token);
        else if (config.Mode == NetworkMode.Client)
            UnavailableMessage = "This PC is not connected to a fax server yet. Connect it in Settings > Network.";
    }

    public bool IsConnected { get; private set; }
    public string UnavailableMessage { get; private set; } = "Connecting to the fax server…";
    public event Action<bool>? ConnectionChanged;
    public event Action<PipeEnvelope>? EventReceived;

    /// <summary>The API client. Failing here gives every page the same message when this PC was never paired.</summary>
    public FaxServerClient Server => _server ?? throw new FaxServerException(UnavailableMessage);

    public void Start()
    {
        if (_server is not null) _loop ??= Task.Run(() => RunAsync(_server, _cts.Token));
    }

    private async Task RunAsync(FaxServerClient server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await server.ReadEventsAsync(e => EventReceived?.Invoke(e), () => SetConnected(true), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (FaxServerException ex)
            {
                SetDisconnected(ex.IsUnauthorized ? ex.Message : $"Could not reach the fax server at {_config.ServerAddress}. Faxes cannot be sent or viewed until it is back; drafts can still be edited.");
            }
            try { await Task.Delay(RetryDelay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        ConnectionChanged?.Invoke(value);
    }

    /// <summary>Also reports a failed first attempt, which is not a change of state but is news to the user.</summary>
    private void SetDisconnected(string message)
    {
        bool changed = IsConnected || message != UnavailableMessage;
        IsConnected = false;
        UnavailableMessage = message;
        if (changed) ConnectionChanged?.Invoke(false);
    }

    public async Task<T?> RequestAsync<T>(string type, object? payload = null, TimeSpan? timeout = null)
    {
        object? result = type switch
        {
            PipeCommands.GetStatus => await Server.GetStatusAsync(),
            PipeCommands.CancelJob => await Server.OutboundActionAsync(Job(payload), "cancel"),
            PipeCommands.PauseJob => await Server.OutboundActionAsync(Job(payload), "pause"),
            PipeCommands.ResumeJob => await Server.OutboundActionAsync(Job(payload), "resume"),
            PipeCommands.RetryJob => await Server.OutboundActionAsync(Job(payload), "retry"),
            _ => throw new InvalidOperationException("This is not available on a client PC."),
        };
        return result is T value ? value : default;
    }

    public async Task<bool> TrySendAsync(string type, object? payload = null)
    {
        if (!IsConnected || type != PipeCommands.AnswerCall) return false;
        try { await Server.AnswerCallAsync(); return true; }
        catch (FaxServerException) { return false; }
    }

    private static Guid Job(object? payload) => payload is JobRef job ? job.JobId : throw new ArgumentException("Missing job id.");

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) { try { await _loop; } catch (Exception ex) when (ex is OperationCanceledException or FaxServerException) { } }
        _server?.Dispose();
    }
}
