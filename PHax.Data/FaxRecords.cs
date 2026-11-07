using PHax.Core.Models;
using PHax.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace PHax.Data;

/// <summary>
/// Changes to stored faxes that behave the same whether they come from the desktop app on this PC or from a paired
/// client through the service.
/// </summary>
public sealed class FaxRecords(IDbContextFactory<FaxDbContext> dbFactory, AppPaths paths, SettingsStore settingsStore)
{
    /// <summary>Creates a new queued job that sends an existing document again.</summary>
    /// <param name="prepare">Fills in the identity of the new job (its id and who submitted it).</param>
    public async Task<OutboundFax> ResendAsync(Guid sourceId, Action<OutboundFax>? prepare = null, CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var src = await db.OutboundFaxes.AsNoTracking().FirstAsync(f => f.Id == sourceId, ct);
        var job = new OutboundFax
        {
            RecipientName = src.RecipientName, FaxNumber = src.FaxNumber, Subject = src.Subject,
            IncludeCoverPage = src.IncludeCoverPage, CoverMessage = src.CoverMessage, PageCount = src.PageCount,
            Status = OutboundStatus.Queued, QueuedUtc = DateTime.UtcNow,
            MaxAttempts = settings.Redial.Enabled ? Math.Max(1, settings.Redial.MaxAttempts) : 1,
        };
        prepare?.Invoke(job);
        var doc = Path.Combine(paths.JobDirectory(job.Id), "fax.tif");
        Directory.CreateDirectory(Path.GetDirectoryName(doc)!);
        File.Copy(paths.Resolve(src.DocumentPath!), doc);
        job.DocumentPath = paths.MakeRelative(doc);
        db.OutboundFaxes.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task DeleteOutboundAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var job = await db.OutboundFaxes.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (job is null) return;
        if (job.Status is OutboundStatus.Dialing or OutboundStatus.Transmitting) throw new InvalidOperationException("Cancel the transmission first.");
        db.OutboundFaxes.Remove(job);
        await db.SaveChangesAsync(ct);
        try { Directory.Delete(paths.JobDirectory(id), recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public async Task DeleteReceivedAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var fax = await db.ReceivedFaxes.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (fax is null) return;
        if (fax.TiffPath is not null) TryDelete(paths.Resolve(fax.TiffPath));
        if (fax.PdfPath is not null) TryDelete(paths.Resolve(fax.PdfPath));
        db.ReceivedFaxes.Remove(fax);
        await db.SaveChangesAsync(ct);
    }

    public static async Task RememberRecipientAsync(FaxDbContext db, OutboundFax job, CancellationToken ct = default)
    {
        var existing = await db.Recipients.FirstOrDefaultAsync(r => r.FaxNumber == job.FaxNumber, ct);
        if (existing is null) db.Recipients.Add(new Recipient { Name = job.RecipientName, FaxNumber = job.FaxNumber });
        else
        {
            existing.LastUsedUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(job.RecipientName)) existing.Name = job.RecipientName;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
