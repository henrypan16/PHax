using PHax.Core.Ipc;
using PHax.Core.Models;
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

/// <summary>Persists drafts and prepares fax documents (rasterisation, cover page, T.6 TIFF) before queuing.</summary>
public sealed class FaxDocumentService(IDbContextFactory<FaxDbContext> dbFactory, AppPaths paths, SettingsStore settingsStore, ServiceClient client)
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
                pages.Insert(0, CoverPageRenderer.Render(new CoverPageInfo(job.RecipientName, job.FaxNumber,
                    data.CoverFrom ?? settings.Outgoing.CoverPageFrom, data.CoverFromFax ?? settings.Incoming.LocalStationId,
                    job.Subject, job.CoverMessage, pages.Count + 1, DateTime.Now)
                {
                    ToAddress = data.CoverToAddress ?? "",
                    FromAddress = data.CoverFromAddress ?? "",
                    FromPhone = data.CoverFromPhone ?? "",
                }));
            }
            progress?.Report("Encoding fax document…");
            var doc = Path.Combine(jobDir, "fax.tif");
            await Task.Run(() => TiffFax.Write(doc, pages));
            job.DocumentPath = paths.MakeRelative(doc);
            job.PageCount = pages.Count;
            job.Status = OutboundStatus.Queued;
            job.QueuedUtc = DateTime.UtcNow;
            job.MaxAttempts = settings.Redial.Enabled ? Math.Max(1, settings.Redial.MaxAttempts) : 1;
            await RememberRecipientAsync(db, job);
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

    /// <summary>Creates a new queued job that resends an existing document (used by Resend).</summary>
    public async Task<OutboundFax> ResendAsync(Guid sourceId)
    {
        var settings = await settingsStore.LoadAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var src = await db.OutboundFaxes.AsNoTracking().FirstAsync(f => f.Id == sourceId);
        var job = new OutboundFax
        {
            RecipientName = src.RecipientName, FaxNumber = src.FaxNumber, Subject = src.Subject,
            IncludeCoverPage = src.IncludeCoverPage, CoverMessage = src.CoverMessage, PageCount = src.PageCount,
            Status = OutboundStatus.Queued, QueuedUtc = DateTime.UtcNow,
            MaxAttempts = settings.Redial.Enabled ? Math.Max(1, settings.Redial.MaxAttempts) : 1,
        };
        var doc = Path.Combine(paths.JobDirectory(job.Id), "fax.tif");
        Directory.CreateDirectory(Path.GetDirectoryName(doc)!);
        File.Copy(paths.Resolve(src.DocumentPath!), doc);
        job.DocumentPath = paths.MakeRelative(doc);
        db.OutboundFaxes.Add(job);
        await db.SaveChangesAsync();
        await client.TrySendAsync(PipeCommands.JobSubmitted, new JobRef(job.Id));
        return job;
    }

    public async Task DeleteOutboundAsync(Guid id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var job = await db.OutboundFaxes.FirstOrDefaultAsync(f => f.Id == id);
        if (job is null) return;
        if (job.Status is OutboundStatus.Dialing or OutboundStatus.Transmitting) throw new InvalidOperationException("Cancel the transmission first.");
        db.OutboundFaxes.Remove(job);
        await db.SaveChangesAsync();
        try { Directory.Delete(paths.JobDirectory(id), recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public async Task DeleteReceivedAsync(Guid id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var fax = await db.ReceivedFaxes.FirstOrDefaultAsync(f => f.Id == id);
        if (fax is null) return;
        if (fax.TiffPath is not null) TryDelete(paths.Resolve(fax.TiffPath));
        if (fax.PdfPath is not null) TryDelete(paths.Resolve(fax.PdfPath));
        db.ReceivedFaxes.Remove(fax);
        await db.SaveChangesAsync();
    }

    public List<FaxBitmap> LoadPages(string storedPath) => TiffFax.Read(paths.Resolve(storedPath));

    private static async Task RememberRecipientAsync(FaxDbContext db, OutboundFax job)
    {
        var existing = await db.Recipients.FirstOrDefaultAsync(r => r.FaxNumber == job.FaxNumber);
        if (existing is null) db.Recipients.Add(new Recipient { Name = job.RecipientName, FaxNumber = job.FaxNumber });
        else
        {
            existing.LastUsedUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(job.RecipientName)) existing.Name = job.RecipientName;
        }
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
