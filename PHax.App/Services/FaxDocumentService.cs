using PHax.Core.Ipc;
using PHax.Core.Models;
using PHax.Core.Remote;
using PHax.Core.Services;
using PHax.Data;
using PHax.Imaging.Conversion;
using PHax.Imaging.Encoding;
using Microsoft.EntityFrameworkCore;

namespace PHax.App.Services;

public sealed class ComposeAttachment
{
    public Guid? ExistingId { get; init; }
    public required string FileName { get; init; }
    /// <summary>Source path for new files, or the stored copy for files already saved with a draft.</summary>
    public required string Path { get; init; }
    public int PageCount { get; set; }
}

public sealed class ComposeData
{
    public Guid? DraftId { get; init; }
    public string RecipientName { get; init; } = "";
    public string FaxNumber { get; init; } = "";
    public string Subject { get; init; } = "";
    public bool IncludeCoverPage { get; init; }
    public string CoverMessage { get; init; } = "";
    public IReadOnlyList<ComposeAttachment> Attachments { get; init; } = [];

    // Cover page overrides (used by integrations); null = use the values from Settings.
    public string? CoverFrom { get; init; }
    public string? CoverFromFax { get; init; }
    public string? CoverFromPhone { get; init; }
    public string? CoverFromAddress { get; init; }
    public string? CoverToAddress { get; init; }
}

/// <summary>The fax could not be handed to the server, so it was kept as the draft <see cref="DraftId"/>.</summary>
public sealed class FaxNotSubmittedException(Guid draftId, string message, Exception inner) : Exception(message, inner)
{
    public Guid DraftId { get; } = draftId;
}

/// <summary>Persists drafts and prepares fax documents (rasterisation, cover page, T.6 TIFF) before queuing.</summary>
public sealed class FaxDocumentService(
    IDbContextFactory<FaxDbContext> dbFactory,
    AppPaths paths,
    SettingsStore settingsStore,
    IServiceConnection client,
    IFaxData faxData,
    FaxRecords records,
    NetworkConfig network,
    RemoteConnection remote)
{
    public async Task<OutboundFax> SaveAsync(ComposeData data, bool queue, IProgress<string>? progress = null)
    {
        var settings = await settingsStore.LoadAsync();
        paths.SetStorageRoot(settings.General.StorageDirectory);
        await using var db = await dbFactory.CreateDbContextAsync();

        OutboundFax job;
        if (data.DraftId is { } id)
        {
            job = await db.OutboundFaxes.Include(f => f.Attachments).FirstAsync(f => f.Id == id);
            if (job.Status != OutboundStatus.Draft) throw new InvalidOperationException("Only drafts can be edited.");
        }
        else
        {
            job = new OutboundFax();
            db.OutboundFaxes.Add(job);
        }
        job.RecipientName = data.RecipientName.Trim();
        job.FaxNumber = data.FaxNumber.Trim();
        job.Subject = data.Subject.Trim();
        job.IncludeCoverPage = data.IncludeCoverPage;
        job.CoverMessage = data.CoverMessage;
        job.UpdatedUtc = DateTime.UtcNow;

        // Preserve originals: copy new files into the job folder; drop removed ones.
        var jobDir = paths.JobDirectory(job.Id);
        var originals = Path.Combine(jobDir, "originals");
        Directory.CreateDirectory(originals);
        var keep = data.Attachments.Where(a => a.ExistingId is not null).Select(a => a.ExistingId!.Value).ToHashSet();
        foreach (var removed in job.Attachments.Where(a => !keep.Contains(a.Id)).ToList())
        {
            TryDelete(paths.Resolve(removed.StoredPath));
            job.Attachments.Remove(removed);
            db.Attachments.Remove(removed);
        }
        int order = 0;
        foreach (var a in data.Attachments)
        {
            if (a.ExistingId is { } existing)
            {
                job.Attachments.First(x => x.Id == existing).Order = order++;
                continue;
            }
            var stored = UniquePath(originals, a.FileName);
            File.Copy(a.Path, stored);
            job.Attachments.Add(new FaxAttachment
            {
                OutboundFaxId = job.Id,
                Order = order++,
                OriginalFileName = a.FileName,
                StoredPath = paths.MakeRelative(stored),
                ContentType = DocumentRasterizer.ContentType(a.FileName),
                PageCount = a.PageCount,
                SizeBytes = new FileInfo(stored).Length,
            });
        }

        if (queue)
        {
            if (!DialString.IsValid(job.FaxNumber)) throw new InvalidOperationException("Enter a valid fax number.");
            if (job.Attachments.Count == 0 && !job.IncludeCoverPage) throw new InvalidOperationException("Add an attachment or a cover page.");
            var rasterizer = new DocumentRasterizer();
            var pages = new List<FaxBitmap>();
            foreach (var att in job.Attachments.OrderBy(a => a.Order))
            {
                progress?.Report($"Converting {att.OriginalFileName}…");
                var p = await Task.Run(() => rasterizer.RasterizeAsync(paths.Resolve(att.StoredPath)));
                att.PageCount = p.Count;
                pages.AddRange(p);
            }
            if (job.IncludeCoverPage)
            {
                progress?.Report("Rendering cover page…");
                pages.Insert(0, RenderCover(data, await faxData.GetCoverDefaultsAsync(), pages.Count + 1));
            }
            progress?.Report("Encoding fax document…");
            var doc = Path.Combine(jobDir, "fax.tif");
            await Task.Run(() => TiffFax.Write(doc, pages));
            job.DocumentPath = paths.MakeRelative(doc);
            job.PageCount = pages.Count;
            if (network.Mode == NetworkMode.Client) return await SubmitToServerAsync(db, job, doc, progress);
            job.Status = OutboundStatus.Queued;
            job.QueuedUtc = DateTime.UtcNow;
            job.MaxAttempts = settings.Redial.Enabled ? Math.Max(1, settings.Redial.MaxAttempts) : 1;
            await FaxRecords.RememberRecipientAsync(db, job);
        }
        else
        {
            job.Status = OutboundStatus.Draft;
        }
        await db.SaveChangesAsync();
        if (queue) await client.TrySendAsync(PipeCommands.JobSubmitted, new JobRef(job.Id));
        return job;
    }

    /// <summary>
    /// Hands a prepared fax to the server. The fax stays a local draft until the server has it, and the job id goes
    /// with it, so sending the same draft again after a lost response cannot dial twice. Afterwards nothing is left on
    /// this PC: the server owns the fax from then on.
    /// </summary>
    private async Task<OutboundFax> SubmitToServerAsync(FaxDbContext db, OutboundFax job, string document, IProgress<string>? progress)
    {
        job.Status = OutboundStatus.Draft;
        await db.SaveChangesAsync();
        progress?.Report("Sending to the fax server…");
        OutboundFax queued;
        try
        {
            await using var file = File.OpenRead(document);
            queued = await remote.Server.SubmitAsync(job.Id, new SubmitFaxRequest(job.RecipientName, job.FaxNumber, job.Subject, job.IncludeCoverPage, job.CoverMessage), file);
        }
        catch (FaxServerException ex)
        {
            throw new FaxNotSubmittedException(job.Id, $"{ex.Message} The fax was kept in Drafts.", ex);
        }
        await records.DeleteOutboundAsync(job.Id);
        return queued;
    }

    /// <summary>
    /// Renders the pages exactly as <see cref="SaveAsync"/> would queue them (cover page first, then every attachment
    /// in order) without saving anything.
    /// </summary>
    public async Task<List<FaxBitmap>> RenderPreviewAsync(ComposeData data, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var rasterizer = new DocumentRasterizer();
        var pages = new List<FaxBitmap>();
        foreach (var a in data.Attachments)
        {
            progress?.Report($"Converting {a.FileName}…");
            pages.AddRange(await Task.Run(() => rasterizer.RasterizeAsync(a.Path, ct), ct));
        }
        if (data.IncludeCoverPage)
        {
            progress?.Report("Rendering cover page…");
            pages.Insert(0, RenderCover(data, await faxData.GetCoverDefaultsAsync(), pages.Count + 1));
        }
        return pages;
    }

    private static FaxBitmap RenderCover(ComposeData data, CoverDefaults defaults, int totalPages) =>
        CoverPageRenderer.Render(new CoverPageInfo(data.RecipientName.Trim(), data.FaxNumber.Trim(),
            data.CoverFrom ?? defaults.From, data.CoverFromFax ?? defaults.StationId,
            data.Subject.Trim(), data.CoverMessage, totalPages, DateTime.Now)
        {
            ToAddress = data.CoverToAddress ?? "",
            FromAddress = data.CoverFromAddress ?? "",
            FromPhone = data.CoverFromPhone ?? "",
        });

    /// <summary>
    /// Records a fax that could not be prepared (e.g. its document is missing) as a Failed job in the Outbox, so the
    /// error is visible next to the other outgoing faxes. Nothing is dialed.
    /// </summary>
    public async Task<OutboundFax> CreateFailedAsync(ComposeData data, string error)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var job = new OutboundFax
        {
            RecipientName = data.RecipientName.Trim(),
            FaxNumber = data.FaxNumber.Trim(),
            Subject = data.Subject.Trim(),
            IncludeCoverPage = data.IncludeCoverPage,
            CoverMessage = data.CoverMessage,
            Status = OutboundStatus.Failed,
            LastOutcome = FaxOutcome.PermanentFailure,
            LastError = error,
            MaxAttempts = 0,
        };
        db.OutboundFaxes.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private static string UniquePath(string dir, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var path = Path.Combine(dir, fileName);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{name} ({i}){ext}");
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
