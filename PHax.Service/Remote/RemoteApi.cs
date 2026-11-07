using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using PHax.Core.Ipc;
using PHax.Core.Models;
using PHax.Core.Remote;
using PHax.Core.Services;
using PHax.Data;
using PHax.Imaging.Encoding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace PHax.Service.Remote;

/// <summary>
/// The HTTPS API paired PCs use. The Inbox is shared by every paired PC; outgoing faxes belong to the PC that submitted
/// them. Whether a fax counts as Sent is still decided only by <see cref="OutboundProcessor"/>: nothing here can set it.
/// </summary>
public sealed class RemoteApi(
    IDbContextFactory<FaxDbContext> dbFactory,
    AppPaths paths,
    SettingsStore settingsStore,
    StatusHolder status,
    EventBus bus,
    ModemSupervisor supervisor,
    CommandHandler commands,
    PairingService pairing,
    FaxRecords records,
    ILogger<RemoteApi> log)
{
    /// <summary>Upload limit, far above any real fax: a page of dithered photo is around 100 KB of T.6.</summary>
    public const int MaxUploadBytes = 64 * 1024 * 1024;
    private const int MaxPages = 1000;
    private const string ClientKey = "PHax.Client";
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(1);

    /// <summary>Events a client may see. Protocol traces stay on the server: they carry numbers and modem detail nobody on a client needs.</summary>
    private static readonly HashSet<string> SharedEvents =
        [PipeEvents.Status, PipeEvents.JobChanged, PipeEvents.JobProgress, PipeEvents.FaxReceived, PipeEvents.InboxChanged, PipeEvents.Notification];

    private static readonly Dictionary<string, string> Actions = new()
    {
        ["cancel"] = PipeCommands.CancelJob, ["pause"] = PipeCommands.PauseJob,
        ["resume"] = PipeCommands.ResumeJob, ["retry"] = PipeCommands.RetryJob,
    };

    public void Map(WebApplication app, string thumbprint)
    {
        app.Use(GuardAsync);

        app.MapGet("/v1/pair", () => Results.NoContent());
        app.MapPost("/v1/pair", async (PairRequest request, HttpContext ctx) =>
        {
            var name = request.ClientName.Trim();
            if (name.Length is 0 or > 100) return Error(400, "Invalid PC name.");
            var result = await pairing.TryPairAsync(name, request.Proof, thumbprint, ctx.RequestAborted);
            log.LogInformation("Pairing from {Address} as {Client}: {Result}", ctx.Connection.RemoteIpAddress, name, result is null ? "refused" : "accepted");
            return result is null ? Error(403, "The code is wrong or has expired. Create a new one on the server.") : Ok(result);
        });

        app.MapGet("/v1/status", () => Ok(status.Current));
        app.MapGet("/v1/config", async (CancellationToken ct) =>
        {
            var s = await settingsStore.LoadAsync(ct);
            return Ok(new RemoteConfigDto(Environment.MachineName, s.Outgoing.CoverPageFrom, s.Incoming.LocalStationId, s.Outgoing.DefaultCoverPage));
        });
        app.MapGet("/v1/events", StreamEventsAsync);
        app.MapPost("/v1/answer", () =>
        {
            supervisor.Enqueue(new AnswerRequest());
            return Results.NoContent();
        });
        app.MapGet("/v1/recipients", async (CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return Ok(await db.Recipients.AsNoTracking().OrderByDescending(r => r.LastUsedUtc).Take(50).ToListAsync(ct));
        });

        MapOutbound(app);
        MapInbound(app);
    }

    private void MapOutbound(WebApplication app)
    {
        app.MapGet("/v1/outbound", async (OutboundView view, string? search, HttpContext ctx) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            var me = Caller(ctx).Id;
            var q = db.OutboundFaxes.AsNoTracking().Where(f => f.SubmittedByClientId == me);
            q = view == OutboundView.Sent ? q.Where(f => f.Status == OutboundStatus.Sent) : q.Where(f => f.Status != OutboundStatus.Sent && f.Status != OutboundStatus.Draft);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                q = q.Where(f => f.RecipientName.Contains(s) || f.FaxNumber.Contains(s) || f.Subject.Contains(s));
            }
            var rows = await q.OrderByDescending(f => f.UpdatedUtc).Take(500).ToListAsync(ctx.RequestAborted);
            return Ok(rows.ConvertAll(ToWire));
        });

        app.MapGet("/v1/outbound/{id:guid}", async (Guid id, HttpContext ctx) =>
            await FindOwnAsync(id, ctx) is { } job ? Ok(ToWire(job)) : NotFound());

        app.MapPut("/v1/outbound/{id:guid}", SubmitAsync);

        app.MapPost("/v1/outbound/{id:guid}/{action}", async (Guid id, string action, HttpContext ctx) =>
        {
            if (!Actions.TryGetValue(action, out var command) || await FindOwnAsync(id, ctx) is null) return NotFound();
            log.LogInformation("{Client} requested {Action} on fax {Id}", Caller(ctx).Name, action, id);
            var result = await commands.HandleAsync(command, PipeProtocol.ToElement(new JobRef(id)), ctx.RequestAborted);
            return Ok(new MessageDto(PipeProtocol.FromElement<string>(result) ?? ""));
        });

        app.MapPost("/v1/outbound/{id:guid}/resend/{newId:guid}", async (Guid id, Guid newId, HttpContext ctx) =>
        {
            var caller = Caller(ctx);
            if (await FindOwnAsync(id, ctx) is not { DocumentPath: not null }) return NotFound();
            // A repeated request must not dial twice; the new id is the client's, as with an upload.
            if (await FindOwnAsync(newId, ctx) is { } existing) return Ok(ToWire(existing));
            var job = await records.ResendAsync(id, j => { j.Id = newId; j.SubmittedByClientId = caller.Id; j.SubmittedBy = caller.Name; }, ctx.RequestAborted);
            Queued(job);
            return Ok(ToWire(job));
        });

        app.MapDelete("/v1/outbound/{id:guid}", async (Guid id, HttpContext ctx) =>
        {
            if (await FindOwnAsync(id, ctx) is null) return Results.NoContent();
            try { await records.DeleteOutboundAsync(id, ctx.RequestAborted); }
            catch (InvalidOperationException ex) { return Error(400, ex.Message); }
            log.LogInformation("{Client} deleted fax {Id}", Caller(ctx).Name, id);
            bus.Publish(PipeEvents.JobChanged, new JobRef(id));
            return Results.NoContent();
        });

        app.MapGet("/v1/outbound/{id:guid}/document", async (Guid id, HttpContext ctx) =>
        {
            var job = await FindOwnAsync(id, ctx);
            if (job?.DocumentPath is null) return NotFound();
            return Document(job.DocumentPath);
        });
    }

    private void MapInbound(WebApplication app)
    {
        app.MapGet("/v1/inbound", async (bool includeIncomplete, string? search, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var q = db.ReceivedFaxes.AsNoTracking().AsQueryable();
            q = includeIncomplete ? q.Where(r => r.Status != ReceivedStatus.Receiving) : q.Where(r => r.Status == ReceivedStatus.Complete);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                q = q.Where(r => r.DisplayName.Contains(s) || (r.CallerId ?? "").Contains(s) || (r.RemoteId ?? "").Contains(s) || (r.CallerName ?? "").Contains(s));
            }
            var rows = await q.OrderByDescending(r => r.ReceivedUtc).Take(includeIncomplete ? 30 : 1000).ToListAsync(ct);
            return Ok(rows.ConvertAll(ToWire));
        });

        app.MapGet("/v1/inbound/unread", async (CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return Ok(new CountDto(await db.ReceivedFaxes.CountAsync(r => r.Status == ReceivedStatus.Complete && !r.IsRead, ct)));
        });

        app.MapPost("/v1/inbound/{id:guid}/read", async (Guid id, ReadRequest request, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.ReceivedFaxes.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsRead, request.IsRead), ct);
            bus.Publish(PipeEvents.InboxChanged, new JobRef(id));
            return Results.NoContent();
        });

        app.MapPost("/v1/inbound/{id:guid}/rename", async (Guid id, RenameRequest request, CancellationToken ct) =>
        {
            var name = request.Name.Trim();
            if (name.Length is 0 or > 200) return Error(400, "Invalid name.");
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.ReceivedFaxes.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.DisplayName, name), ct);
            bus.Publish(PipeEvents.InboxChanged, new JobRef(id));
            return Results.NoContent();
        });

        app.MapDelete("/v1/inbound/{id:guid}", async (Guid id, HttpContext ctx) =>
        {
            await records.DeleteReceivedAsync(id, ctx.RequestAborted);
            log.LogInformation("{Client} deleted received fax {Id}", Caller(ctx).Name, id);
            bus.Publish(PipeEvents.InboxChanged, new JobRef(id));
            return Results.NoContent();
        });

        app.MapGet("/v1/inbound/{id:guid}/document", async (Guid id, HttpContext ctx) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
            var fax = await db.ReceivedFaxes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ctx.RequestAborted);
            if (fax?.TiffPath is null) return NotFound();
            log.LogInformation("{Client} opened received fax {Id}", Caller(ctx).Name, id);
            return Document(fax.TiffPath);
        });
    }

    /// <summary>Rejects addresses outside the local network, then authenticates everything except pairing.</summary>
    private async Task GuardAsync(HttpContext ctx, RequestDelegate next)
    {
        if (!LocalNetwork.IsLocal(ctx.Connection.RemoteIpAddress))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        try
        {
            if (!ctx.Request.Path.StartsWithSegments("/v1/pair"))
            {
                var client = await AuthenticateAsync(ctx);
                if (client is null)
                {
                    await Error(401, "This PC is not connected to the server. Connect it in Settings > Network.").ExecuteAsync(ctx);
                    return;
                }
                ctx.Items[ClientKey] = client;
            }
            await next(ctx);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
        catch (JsonException) { if (!ctx.Response.HasStarted) await Error(400, "Malformed request.").ExecuteAsync(ctx); }
        catch (BadHttpRequestException) { if (!ctx.Response.HasStarted) await Error(400, "Malformed request.").ExecuteAsync(ctx); }
        catch (Exception ex)
        {
            log.LogError(ex, "Request {Method} {Path} failed", ctx.Request.Method, ctx.Request.Path);
            if (!ctx.Response.HasStarted) await Error(500, "The server could not complete the request.").ExecuteAsync(ctx);
        }
    }

    private async Task<PairedClient?> AuthenticateAsync(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var hash = PairingService.HashToken(header["Bearer ".Length..]);
        await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
        var client = await db.PairedClients.FirstOrDefaultAsync(c => c.TokenHash == hash, ctx.RequestAborted);
        if (client is null)
        {
            log.LogWarning("Rejected a request with an unknown token from {Address}", ctx.Connection.RemoteIpAddress);
            return null;
        }
        if (client.LastSeenUtc is null || DateTime.UtcNow - client.LastSeenUtc > LastSeenResolution)
        {
            client.LastSeenUtc = DateTime.UtcNow;
            client.LastAddress = ctx.Connection.RemoteIpAddress?.ToString();
            await db.SaveChangesAsync(ctx.RequestAborted);
        }
        return client;
    }

    /// <summary>
    /// Stores and queues a prepared fax. The client chooses the id, which makes this idempotent: the same upload again
    /// returns the existing job instead of dialing twice, and different content under that id is refused.
    /// </summary>
    private async Task<IResult> SubmitAsync(Guid id, HttpContext ctx)
    {
        var caller = Caller(ctx);
        var ct = ctx.RequestAborted;
        ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = MaxUploadBytes;
        // Kept in memory: spilling to disk would leave document contents in the service's temp folder.
        var form = await ctx.Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = MaxUploadBytes, MemoryBufferThreshold = MaxUploadBytes }, ct);
        var fax = form.TryGetValue("fax", out var json) ? JsonSerializer.Deserialize<SubmitFaxRequest>(json.ToString(), PipeProtocol.Json) : null;
        var file = form.Files.GetFile("document");
        if (fax is null || file is null) return Error(400, "The upload is incomplete.");

        var number = fax.FaxNumber.Trim();
        if (!DialString.IsValid(number) || number.Length > 64) return Error(400, "Enter a valid fax number.");
        if (fax.RecipientName.Length > 200 || fax.Subject.Length > 500 || fax.CoverMessage.Length > 20_000) return Error(400, "The fax details are too long.");

        using var buffer = new MemoryStream((int)Math.Min(file.Length, MaxUploadBytes));
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData([.. bytes, .. System.Text.Encoding.UTF8.GetBytes(number)]));

        if (await ExistingSubmissionAsync(id, caller, hash, ct) is { } repeat) return repeat;

        int pages;
        try { pages = await Task.Run(() => TiffFax.ValidateUpload(bytes, MaxPages), ct); }
        catch (InvalidDataException ex)
        {
            log.LogWarning("Rejected a document from {Client}: {Reason}", caller.Name, ex.Message);
            return Error(400, ex.Message);
        }

        var settings = await settingsStore.LoadAsync(ct);
        var doc = Path.Combine(paths.JobDirectory(id), "fax.tif");
        Directory.CreateDirectory(Path.GetDirectoryName(doc)!);
        await File.WriteAllBytesAsync(doc, bytes, ct);
        var job = new OutboundFax
        {
            Id = id,
            RecipientName = fax.RecipientName.Trim(),
            FaxNumber = number,
            Subject = fax.Subject.Trim(),
            IncludeCoverPage = fax.IncludeCoverPage,
            CoverMessage = fax.CoverMessage,
            DocumentPath = paths.MakeRelative(doc),
            PageCount = pages,
            Status = OutboundStatus.Queued,
            QueuedUtc = DateTime.UtcNow,
            MaxAttempts = settings.Redial.Enabled ? Math.Max(1, settings.Redial.MaxAttempts) : 1,
            SubmittedByClientId = caller.Id,
            SubmittedBy = caller.Name,
            SubmissionHash = hash,
        };
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.OutboundFaxes.Add(job);
        await FaxRecords.RememberRecipientAsync(db, job, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Two copies of the same upload raced; the primary key let one through.
            if (await ExistingSubmissionAsync(id, caller, hash, ct) is { } raced) return raced;
            throw;
        }

        log.LogInformation("{Client} queued fax {Id} ({Pages} page(s))", caller.Name, id, pages);
        Queued(job);
        return Ok(ToWire(job));
    }

    private async Task<IResult?> ExistingSubmissionAsync(Guid id, PairedClient caller, string hash, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.OutboundFaxes.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (existing is null) return null;
        return existing.SubmittedByClientId == caller.Id && existing.SubmissionHash == hash
            ? Ok(ToWire(existing))
            : Error(409, "The server already has this fax with different content. Check the Outbox before sending it again.");
    }

    private void Queued(OutboundFax job)
    {
        bus.Publish(PipeEvents.JobChanged, new JobRef(job.Id));
        supervisor.Enqueue(new NudgeRequest());
    }

    private async Task StreamEventsAsync(HttpContext ctx)
    {
        var caller = Caller(ctx);
        var lifetime = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, lifetime.ApplicationStopping);
        var channel = Channel.CreateBounded<PipeEnvelope>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        void OnPublished(PipeEnvelope e) { if (SharedEvents.Contains(e.Type)) channel.Writer.TryWrite(e); }
        void OnRemoved(Guid id) { if (id == caller.Id) stream.Cancel(); }
        var ownership = new Dictionary<Guid, bool>();

        bus.Published += OnPublished;
        pairing.ClientRemoved += OnRemoved;
        try
        {
            ctx.Response.ContentType = "application/x-ndjson";
            ctx.Response.Headers.CacheControl = "no-store";
            await ctx.Response.WriteAsync("\n", stream.Token);
            await ctx.Response.Body.FlushAsync(stream.Token);
            while (!stream.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stream.Token);
                wait.CancelAfter(Heartbeat);
                string line = "";
                try
                {
                    var envelope = await channel.Reader.ReadAsync(wait.Token);
                    if (!await VisibleToAsync(caller, envelope, ownership, stream.Token)) continue;
                    line = PipeProtocol.Serialize(envelope);
                }
                catch (OperationCanceledException) when (!stream.IsCancellationRequested) { }
                await ctx.Response.WriteAsync(line + "\n", stream.Token);
                await ctx.Response.Body.FlushAsync(stream.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            bus.Published -= OnPublished;
            pairing.ClientRemoved -= OnRemoved;
        }
    }

    /// <summary>Events about an outgoing fax go only to the PC that submitted it: they can name the recipient.</summary>
    private async Task<bool> VisibleToAsync(PairedClient caller, PipeEnvelope e, Dictionary<Guid, bool> ownership, CancellationToken ct)
    {
        Guid? job = e.Type switch
        {
            PipeEvents.JobChanged => PipeProtocol.FromElement<JobRef>(e.Payload)?.JobId,
            PipeEvents.JobProgress => PipeProtocol.FromElement<JobProgressDto>(e.Payload)?.JobId,
            PipeEvents.Notification => PipeProtocol.FromElement<NotificationDto>(e.Payload)?.RelatedId,
            _ => null,
        };
        if (job is not { } id) return true;
        if (ownership.TryGetValue(id, out var known)) return known;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.OutboundFaxes.AsNoTracking().Where(f => f.Id == id).Select(f => new { f.SubmittedByClientId }).FirstOrDefaultAsync(ct);
        // No outbound row: a notification about a received fax (shared), or a job deleted a moment ago (harmless id only).
        if (row is null) return true;
        return ownership[id] = row.SubmittedByClientId == caller.Id;
    }

    private async Task<OutboundFax?> FindOwnAsync(Guid id, HttpContext ctx)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ctx.RequestAborted);
        var me = Caller(ctx).Id;
        return await db.OutboundFaxes.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id && f.SubmittedByClientId == me, ctx.RequestAborted);
    }

    private IResult Document(string storedPath)
    {
        var path = paths.Resolve(storedPath);
        return File.Exists(path) ? Results.File(path, "image/tiff") : NotFound();
    }

    private static PairedClient Caller(HttpContext ctx) => (PairedClient)ctx.Items[ClientKey]!;

    /// <summary>Server file paths are nobody else's business; clients only need to know a document exists.</summary>
    private static OutboundFax ToWire(OutboundFax f)
    {
        f.DocumentPath = f.DocumentPath is null ? null : RemoteDocument.Marker;
        f.SubmissionHash = null;
        return f;
    }

    private static ReceivedFax ToWire(ReceivedFax f)
    {
        f.TiffPath = f.TiffPath is null ? null : RemoteDocument.Marker;
        f.PdfPath = null;
        f.ProtocolLogPath = null;
        return f;
    }

    private static IResult Ok<T>(T value) => Results.Json(value, PipeProtocol.Json);
    private static IResult NotFound() => Error(404, "The fax was not found.");
    private static IResult Error(int status, string message) => Results.Json(new ErrorDto(message), PipeProtocol.Json, statusCode: status);
}
