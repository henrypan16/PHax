using PHax.Core.Interfaces;
using PHax.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace PHax.Data;

/// <summary>SQLite-backed outbound queue. Only the service calls <see cref="TakeNextDueAsync"/>, so there is a single consumer.</summary>
public sealed class DbFaxJobQueue(IDbContextFactory<FaxDbContext> factory) : IFaxJobQueue
{
    public async Task RecoverAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var interrupted = await db.OutboundFaxes
            .Include(f => f.TransmissionAttempts)
            .Where(f => f.Status == OutboundStatus.Dialing || f.Status == OutboundStatus.Transmitting)
            .ToListAsync(ct);
        foreach (var job in interrupted)
        {
            var attempt = job.TransmissionAttempts.OrderByDescending(a => a.StartedUtc).FirstOrDefault();
            bool dataSent = job.Status == OutboundStatus.Transmitting || attempt?.PageDataTransmitted == true;
            if (attempt is { EndedUtc: null })
            {
                attempt.EndedUtc = DateTime.UtcNow;
                attempt.Outcome = dataSent ? FaxOutcome.UnconfirmedDelivery : FaxOutcome.CommunicationFailure;
                attempt.Message = "Service stopped during the attempt.";
            }
            if (dataSent)
            {
                job.Status = OutboundStatus.Uncertain;
                job.LastOutcome = FaxOutcome.UnconfirmedDelivery;
                job.LastError = "The service stopped while pages were being transmitted. The receiver may have the fax; retry manually if needed.";
            }
            else
            {
                job.Status = OutboundStatus.Queued;
                job.LastError = "The service stopped before page transmission; requeued.";
            }
            job.UpdatedUtc = DateTime.UtcNow;
        }

        var receiving = await db.ReceivedFaxes.Where(r => r.Status == ReceivedStatus.Receiving).ToListAsync(ct);
        foreach (var r in receiving)
        {
            r.Status = r.PageCount > 0 ? ReceivedStatus.Partial : ReceivedStatus.Failed;
            r.ErrorMessage = "The service stopped during reception.";
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<OutboundFax?> TakeNextDueAsync(DateTime utcNow, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.OutboundFaxes
            .Where(f => f.Status == OutboundStatus.Queued || (f.Status == OutboundStatus.RetryWaiting && f.NextAttemptUtc <= utcNow))
            .OrderBy(f => f.NextAttemptUtc ?? f.QueuedUtc ?? f.CreatedUtc)
            .FirstOrDefaultAsync(ct);
        if (job is null) return null;
        job.Status = OutboundStatus.Dialing;
        job.UpdatedUtc = utcNow;
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<DateTime?> GetNextDueTimeAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.OutboundFaxes.AnyAsync(f => f.Status == OutboundStatus.Queued, ct)) return DateTime.UtcNow;
        return await db.OutboundFaxes
            .Where(f => f.Status == OutboundStatus.RetryWaiting)
            .MinAsync(f => (DateTime?)f.NextAttemptUtc, ct);
    }
}
