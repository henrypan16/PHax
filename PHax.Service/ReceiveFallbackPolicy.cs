using PHax.Core.Models;
using PHax.Data;
using Microsoft.EntityFrameworkCore;

namespace PHax.Service;

/// <summary>
/// Decides whether an incoming call may be offered V.17. The calling station picks the modulation from our DIS, and some
/// senders that fail at V.17 hang up and redial at V.17 instead of stepping down within the call. After such a failure
/// the next call from the same sender is offered V.29/V.27ter only. The sender's TSI arrives after DIS, so the sender
/// is recognised by caller ID; without caller ID, a recent V.17 failure from an unknown caller is assumed to be the redial.
/// </summary>
public static class ReceiveFallbackPolicy
{
    /// <summary>How long a caller-ID-identified sender keeps getting V.29 after failing at V.17.</summary>
    public static readonly TimeSpan KnownCallerWindow = TimeSpan.FromDays(30);

    /// <summary>Without caller ID, only a failure this recent is treated as the same sender redialing.</summary>
    public static readonly TimeSpan UnknownCallerWindow = TimeSpan.FromMinutes(15);

    /// <summary>Returns why V.17 must not be offered to this call, or null when it may be.</summary>
    public static string? V17BlockReason(string? callerNumber, IEnumerable<ReceivedFax> history, DateTime nowUtc)
    {
        var caller = string.IsNullOrWhiteSpace(callerNumber) ? null : callerNumber.Trim();
        var window = caller is null ? UnknownCallerWindow : KnownCallerWindow;
        var relevant = history
            .Where(r => r.ReceivedUtc >= nowUtc - window && r.ReceivedUtc <= nowUtc)
            .Where(r => caller is null ? string.IsNullOrWhiteSpace(r.CallerId) : string.Equals(r.CallerId?.Trim(), caller, StringComparison.Ordinal))
            .OrderByDescending(r => r.ReceivedUtc)
            .ToList();

        var failure = relevant.FirstOrDefault(IsV17Failure);
        if (failure is null) return null;
        // Without caller ID a later complete reception means the line has moved on to another sender (or the redial worked).
        if (caller is null && relevant.Any(r => r.ReceivedUtc > failure.ReceivedUtc && r.Status == ReceivedStatus.Complete)) return null;

        var who = caller is null ? "an unidentified caller" : $"caller {caller}";
        return $"previous reception from {who} failed at {failure.Modulation} ({failure.ReceivedUtc.ToLocalTime():g})";
    }

    /// <summary>Loads the receptions that <see cref="V17BlockReason(string?, IEnumerable{ReceivedFax}, DateTime)"/> needs.</summary>
    /// <remarks>Simulated and real receptions are kept apart so simulator runs never change what a real caller is offered.</remarks>
    public static async Task<string?> V17BlockReasonAsync(FaxDbContext db, string? callerNumber, bool simulated, DateTime nowUtc, CancellationToken ct)
    {
        var caller = string.IsNullOrWhiteSpace(callerNumber) ? null : callerNumber.Trim();
        var since = nowUtc - (caller is null ? UnknownCallerWindow : KnownCallerWindow);
        var query = db.ReceivedFaxes.AsNoTracking()
            .Where(r => r.ReceivedUtc >= since && r.Status != ReceivedStatus.Receiving && r.IsSimulated == simulated);
        query = caller is null
            ? query.Where(r => r.CallerId == null || r.CallerId == "")
            : query.Where(r => r.CallerId == caller);
        var history = await query.ToListAsync(ct);
        return V17BlockReason(caller, history, nowUtc);
    }

    private static bool IsV17Failure(ReceivedFax r) =>
        r.Status is ReceivedStatus.Failed or ReceivedStatus.Partial
        && r.Modulation is { } m && m.StartsWith("V.17", StringComparison.Ordinal);
}
