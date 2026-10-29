using PHax.Core.Models;

namespace PHax.Core.Services;

public sealed record RetryDecision(OutboundStatus Status, DateTime? NextAttemptUtc, string Reason);

/// <summary>
/// Decides what happens to a job after an attempt.
/// Rules: cancelled and permanent failures are never retried; an unconfirmed delivery (page data sent,
/// no MCF) becomes <see cref="OutboundStatus.Uncertain"/> and needs a human decision, to avoid duplicate faxes.
/// </summary>
public static class RetryPolicy
{
    public static RetryDecision Decide(FaxOutcome outcome, int attemptsMade, int maxAttempts, RedialSettings redial, DateTime utcNow)
    {
        switch (outcome)
        {
            case FaxOutcome.Success:
                return new(OutboundStatus.Sent, null, "Delivery confirmed by remote station.");
            case FaxOutcome.Cancelled:
                return new(OutboundStatus.Cancelled, null, "Cancelled.");
            case FaxOutcome.PermanentFailure:
            case FaxOutcome.RemoteRejected:
                return new(OutboundStatus.Failed, null, "Permanent failure; not retried.");
            case FaxOutcome.UnconfirmedDelivery:
                return new(OutboundStatus.Uncertain, null,
                    "Page data was sent but the receiver did not confirm. Not retried automatically to avoid a duplicate fax.");
        }

        bool allowed = redial.Enabled && outcome switch
        {
            FaxOutcome.Busy => redial.RetryOnBusy,
            FaxOutcome.NoAnswer or FaxOutcome.NoFaxAnswer => redial.RetryOnNoAnswer,
            FaxOutcome.NoDialTone or FaxOutcome.TrainingFailed or FaxOutcome.CommunicationFailure
                or FaxOutcome.PartialFailure or FaxOutcome.ModemError or FaxOutcome.ModemDisconnected
                => redial.RetryOnCommunicationFailure,
            _ => false,
        };

        int limit = Math.Max(1, Math.Min(maxAttempts, redial.MaxAttempts));
        if (!allowed)
            return new(OutboundStatus.Failed, null, "Automatic redial disabled for this failure type.");
        if (attemptsMade >= limit)
            return new(OutboundStatus.Failed, null, $"Gave up after {attemptsMade} attempt(s).");

        var delay = TimeSpan.FromMinutes(Math.Max(1, redial.DelayMinutes));
        return new(OutboundStatus.RetryWaiting, utcNow + delay, $"Retry {attemptsMade + 1} of {limit} scheduled.");
    }
}
