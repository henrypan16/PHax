using System.Collections.Concurrent;
using PHax.Core.Interfaces;
using PHax.Core.Ipc;
using PHax.Core.Models;
using PHax.Core.Services;
using PHax.Data;
using PHax.Imaging.Encoding;
using Microsoft.EntityFrameworkCore;

namespace PHax.Service;

/// <summary>Runs one outbound attempt for a job and persists the outcome.</summary>
public sealed class OutboundProcessor(
    IDbContextFactory<FaxDbContext> dbFactory,
    AppPaths paths,
    StatusHolder status,
    EventBus bus,
    Notifier notifier,
    ServiceProtocolLog protocol,
    ILogger<OutboundProcessor> log)
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();

    public bool TryCancelActive(Guid jobId)
    {
        if (!_active.TryGetValue(jobId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    public bool IsActive(Guid jobId) => _active.ContainsKey(jobId);

    public async Task SendAsync(IFaxModem modem, IClass1FaxEngine engine, OutboundFax job, AppSettings settings, CancellationToken stopping)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _active[job.Id] = cts;
        var attempt = new TransmissionAttempt { OutboundFaxId = job.Id, AttemptNumber = job.Attempts + 1 };
        try
        {
            await using (var db = await dbFactory.CreateDbContextAsync(stopping))
            {
                db.TransmissionAttempts.Add(attempt);
                var row = await db.OutboundFaxes.FirstAsync(f => f.Id == job.Id, stopping);
                row.Attempts++;
                row.IsSimulated = modem.IsSimulated;
                row.LastError = null;
                await db.SaveChangesAsync(stopping);
                job = row;
            }
            bus.Publish(PipeEvents.JobChanged, new JobRef(job.Id));

            List<FaxBitmap> pages;
            try
            {
                if (string.IsNullOrEmpty(job.DocumentPath)) throw new FileNotFoundException("The job has no prepared document.");
                pages = TiffFax.Read(paths.Resolve(job.DocumentPath));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                await CompleteAsync(job, attempt, new FaxSendResult { Outcome = FaxOutcome.PermanentFailure, Message = $"Document cannot be read: {ex.Message}", ErrorCode = "DOCUMENT" }, settings, 0);
                return;
            }

            int alreadyConfirmed = Math.Clamp(job.PagesConfirmed, 0, pages.Count);
            var remaining = pages.Skip(alreadyConfirmed).ToList();
            if (alreadyConfirmed > 0)
                protocol.Log.Log("JOB", $"Resuming job {job.Id:N} at page {alreadyConfirmed + 1}; pages 1-{alreadyConfirmed} were confirmed in an earlier call.");

            attempt.ProtocolLogPath = protocol.Log.BeginSessionFile(paths.ProtocolLogsDirectory, $"send-{job.Id:N}-{attempt.AttemptNumber}");
            var sender = engine.CreateSender(modem);
            bool markedTransmitting = false;
            DateTime lastPublish = DateTime.MinValue;
            sender.Progress += (_, p) =>
            {
                if (p.Phase == T30Phase.C && !markedTransmitting)
                {
                    markedTransmitting = true;
                    // Persist before page data goes out: if the service dies now, recovery must treat the job as uncertain.
                    SetStatus(job.Id, OutboundStatus.Transmitting).GetAwaiter().GetResult();
                }
                status.Update(s => s with { Phase = p.Phase, Modulation = p.Modulation ?? s.Modulation, Resolution = p.Resolution ?? s.Resolution, ActiveJobId = job.Id });
                if (DateTime.UtcNow - lastPublish < TimeSpan.FromMilliseconds(250) && p.Bytes != p.TotalBytes) return;
                lastPublish = DateTime.UtcNow;
                int page = p.Page == 0 ? 0 : p.Page + alreadyConfirmed;
                double pageFraction = p.TotalBytes > 0 ? (double)p.Bytes / p.TotalBytes : 0;
                double percent = pages.Count == 0 ? 0 : Math.Min(100, ((page == 0 ? 0 : page - 1) + pageFraction) / pages.Count * 100);
                bus.Publish(PipeEvents.JobProgress, new JobProgressDto(job.Id,
                    p.Phase is T30Phase.C or T30Phase.D ? OutboundStatus.Transmitting : OutboundStatus.Dialing,
                    p.Phase, p.Message, page, pages.Count, percent));
            };

            status.Update(s => s with { ModemState = ModemState.Sending, ActiveJobId = job.Id, Phase = T30Phase.A });
            var request = new FaxSendRequest
            {
                DialString = DialString.Build(settings.Outgoing.DialPrefix, job.FaxNumber),
                Pages = remaining,
                LocalId = DialString.StationId(settings.Incoming.LocalStationId),
                MaxBitRate = settings.Outgoing.MaxSpeed,
                AllowFine = settings.Outgoing.UseFineResolution,
                AllowMr = settings.Outgoing.AllowMrCompression && !settings.Modem.LineCompatibilityMode,
                AllowV17 = !settings.Modem.LineCompatibilityMode,
                ToneDialing = settings.Outgoing.ToneDialing,
                Timeout = TimeSpan.FromSeconds(Math.Max(60, settings.Outgoing.TransmissionTimeoutSeconds)),
            };
            log.LogInformation("Sending job {Job} to {Number} ({Pages} page(s), attempt {Attempt})",
                job.Id, DialString.Redact(request.DialString), remaining.Count, attempt.AttemptNumber);
            var result = await sender.SendAsync(request, cts.Token);
            await CompleteAsync(job, attempt, result, settings, alreadyConfirmed);
        }
        finally
        {
            protocol.Log.EndSessionFile();
            _active.TryRemove(job.Id, out _);
            status.Update(s => s with { ModemState = s.ModemState == ModemState.Sending ? ModemState.Ready : s.ModemState, ActiveJobId = null, Phase = T30Phase.Idle });
        }
    }

    private async Task SetStatus(Guid id, OutboundStatus value)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.OutboundFaxes.Where(f => f.Id == id).ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, value).SetProperty(f => f.UpdatedUtc, DateTime.UtcNow));
        await db.TransmissionAttempts.Where(a => a.OutboundFaxId == id && a.EndedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.PageDataTransmitted, true));
        bus.Publish(PipeEvents.JobChanged, new JobRef(id));
    }

    private async Task CompleteAsync(OutboundFax job, TransmissionAttempt attempt, FaxSendResult result, AppSettings settings, int alreadyConfirmed)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.OutboundFaxes.FirstAsync(f => f.Id == job.Id);
        var att = await db.TransmissionAttempts.FirstAsync(a => a.Id == attempt.Id);
        var now = DateTime.UtcNow;

        att.EndedUtc = now;
        att.Outcome = result.Outcome;
        att.ErrorCode = result.ErrorCode;
        att.Message = result.Message;
        att.Modulation = result.Modulation;
        att.Resolution = result.Resolution;
        att.PagesSent = result.PagesSent;
        att.PagesConfirmed = result.PagesConfirmed;
        att.RemoteId = result.RemoteId;
        att.PageDataTransmitted |= result.PageDataTransmitted;
        att.ProtocolLogPath = attempt.ProtocolLogPath;

        row.PagesConfirmed = alreadyConfirmed + result.PagesConfirmed;
        row.LastOutcome = result.Outcome;
        row.UpdatedUtc = now;
        row.CurrentPage = 0;

        var decision = RetryPolicy.Decide(result.Outcome, row.Attempts, row.MaxAttempts, settings.Redial, now);
        // Never report success unless T.30 confirmation was received for the final page.
        if (decision.Status == OutboundStatus.Sent && !result.DeliveryConfirmed)
            decision = new RetryDecision(OutboundStatus.Uncertain, null, "Session ended without final confirmation.");
        row.Status = decision.Status;
        row.NextAttemptUtc = decision.NextAttemptUtc;

        if (decision.Status == OutboundStatus.Sent)
        {
            row.SentUtc = now;
            row.DurationSeconds = (row.DurationSeconds ?? 0) + result.Duration.TotalSeconds;
            row.RemoteId = result.RemoteId;
            row.ConfirmationDetail = $"MCF received for all {row.PageCount} page(s) at {result.Modulation}" +
                                     (row.Attempts > 1 ? $" (attempt {row.Attempts})" : "") +
                                     (alreadyConfirmed > 0 ? $"; pages 1-{alreadyConfirmed} confirmed in an earlier call" : "");
            row.LastError = null;
        }
        else
        {
            row.DurationSeconds = (row.DurationSeconds ?? 0) + result.Duration.TotalSeconds;
            row.LastError = $"{result.Message} {decision.Reason}".Trim();
        }
        await db.SaveChangesAsync();
        bus.Publish(PipeEvents.JobChanged, new JobRef(row.Id));

        var who = string.IsNullOrWhiteSpace(row.RecipientName) ? row.FaxNumber : row.RecipientName;
        var sim = row.IsSimulated ? " [SIMULATED]" : "";
        switch (decision.Status)
        {
            case OutboundStatus.Sent:
                await notifier.NotifyAsync(NotificationKind.FaxSent, $"Fax sent{sim}", $"{row.PageCount} page(s) delivered to {who}.", row.Id, settings);
                break;
            case OutboundStatus.RetryWaiting:
                await notifier.NotifyAsync(NotificationKind.RetryScheduled, $"Retry scheduled{sim}",
                    $"Fax to {who}: {result.Message} Next attempt at {decision.NextAttemptUtc!.Value.ToLocalTime():t}.", row.Id, settings);
                break;
            case OutboundStatus.Cancelled:
                break;
            default:
                await notifier.NotifyAsync(NotificationKind.FaxFailed, decision.Status == OutboundStatus.Uncertain ? $"Fax delivery unconfirmed{sim}" : $"Fax failed{sim}",
                    $"Fax to {who}: {result.Message}", row.Id, settings);
                break;
        }
    }
}

/// <summary>Runs one inbound session and stores the result.</summary>
public sealed class InboundProcessor(
    IDbContextFactory<FaxDbContext> dbFactory,
    AppPaths paths,
    StatusHolder status,
    EventBus bus,
    Notifier notifier,
    ServiceProtocolLog protocol,
    ILogger<InboundProcessor> log)
{
    public async Task ReceiveAsync(IFaxModem modem, IClass1FaxEngine engine, AppSettings settings, CallInfoDto call, CancellationToken ct)
    {
        var record = new ReceivedFax
        {
            CallerId = call.CallerNumber,
            CallerName = call.CallerName,
            DisplayName = $"Fax from {call.CallerName ?? call.CallerNumber ?? "unknown sender"}",
            IsSimulated = modem.IsSimulated,
        };
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.ReceivedFaxes.Add(record);
            await db.SaveChangesAsync(ct);
        }
        record.ProtocolLogPath = protocol.Log.BeginSessionFile(paths.ProtocolLogsDirectory, $"receive-{record.Id:N}");
        var started = DateTime.UtcNow;
        status.Update(s => s with { ModemState = ModemState.Receiving, Call = call with { State = "Answering", StartedUtc = started, AwaitingManualAnswer = false } });

        try
        {
            var receiver = engine.CreateReceiver(modem);
            receiver.Progress += (_, p) =>
            {
                status.Update(s => s with
                {
                    Phase = p.Phase,
                    Modulation = p.Modulation ?? s.Modulation,
                    Resolution = p.Resolution ?? s.Resolution,
                    Call = (s.Call ?? call) with { State = p.Message, RemoteId = p.RemoteId ?? s.Call?.RemoteId },
                });
                bus.Publish(PipeEvents.ReceptionProgress, new { record.Id, p.Phase, p.Message, p.Page, p.RemoteId, p.Modulation });
            };
            var result = await receiver.ReceiveAsync(new FaxReceiveRequest
            {
                LocalId = DialString.StationId(settings.Incoming.LocalStationId),
                MaxBitRate = settings.Outgoing.MaxSpeed,
                AllowFine = true,
                AllowMr = !settings.Modem.LineCompatibilityMode,
                AllowV17 = !settings.Modem.LineCompatibilityMode,
                PageReceived = (_, n) => status.Update(s => s with { Call = (s.Call ?? call) with { PagesReceived = n } }),
            }, ct);
            await StoreAsync(record, result, settings, started);
        }
        finally
        {
            protocol.Log.EndSessionFile();
            status.Update(s => s with { ModemState = s.ModemState == ModemState.Receiving ? ModemState.Ready : s.ModemState, Call = null, Phase = T30Phase.Idle });
        }
    }

    private async Task StoreAsync(ReceivedFax record, FaxReceiveResult result, AppSettings settings, DateTime started)
    {
        string? tiffPath = null, pdfPath = null;
        if (result.Pages.Count > 0)
        {
            var folder = result.Complete ? paths.InboxDirectory : paths.FailedReceptionsDirectory;
            var who = Sanitize(result.RemoteId ?? record.CallerId ?? "unknown");
            var name = $"{started.ToLocalTime():yyyyMMdd-HHmmss}-{who}";
            tiffPath = Path.Combine(folder, name + ".tif");
            TiffFax.Write(tiffPath, result.Pages);
            if (settings.Incoming.AutoConvertToPdf)
            {
                pdfPath = Path.ChangeExtension(tiffPath, ".pdf");
                PdfFax.Write(pdfPath, result.Pages);
            }
            if (result.Complete && settings.Incoming.AutoSave && !string.IsNullOrWhiteSpace(settings.Incoming.ReceiveFolder))
            {
                try
                {
                    Directory.CreateDirectory(settings.Incoming.ReceiveFolder);
                    File.Copy(tiffPath, Path.Combine(settings.Incoming.ReceiveFolder, Path.GetFileName(tiffPath)), overwrite: true);
                    if (pdfPath is not null) File.Copy(pdfPath, Path.Combine(settings.Incoming.ReceiveFolder, Path.GetFileName(pdfPath)), overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.LogWarning(ex, "Could not copy received fax to {Folder}", settings.Incoming.ReceiveFolder);
                }
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.ReceivedFaxes.FirstAsync(r => r.Id == record.Id);
        row.Status = result.Complete ? ReceivedStatus.Complete : result.Pages.Count > 0 ? ReceivedStatus.Partial : ReceivedStatus.Failed;
        row.PageCount = result.Pages.Count;
        row.RemoteId = result.RemoteId;
        row.TiffPath = tiffPath is null ? null : paths.MakeRelative(tiffPath);
        row.PdfPath = pdfPath is null ? null : paths.MakeRelative(pdfPath);
        row.DurationSeconds = result.Duration.TotalSeconds;
        row.Modulation = result.Modulation;
        row.Resolution = result.Resolution;
        row.ErrorMessage = result.Complete ? null : result.Message;
        row.ProtocolLogPath = record.ProtocolLogPath;
        if (!string.IsNullOrWhiteSpace(result.RemoteId) && record.CallerName is null) row.DisplayName = $"Fax from {result.RemoteId}";
        await db.SaveChangesAsync();
        bus.Publish(PipeEvents.FaxReceived, new JobRef(row.Id));
        log.LogInformation("Reception {Id}: {Status}, {Pages} page(s). {Message}", row.Id, row.Status, row.PageCount, result.Message);

        var sim = row.IsSimulated ? " [SIMULATED]" : "";
        if (row.Status == ReceivedStatus.Complete)
            await notifier.NotifyAsync(NotificationKind.FaxReceived, $"Fax received{sim}", $"{row.DisplayName}: {row.PageCount} page(s).", row.Id, settings);
        else if (row.Status != ReceivedStatus.Complete && result.Outcome != FaxOutcome.NoFaxAnswer)
            await notifier.NotifyAsync(NotificationKind.FaxFailed, $"Fax reception incomplete{sim}", $"{row.DisplayName}: {result.Message}", row.Id, settings);

        if (row.Status == ReceivedStatus.Complete && settings.Incoming.AutoPrint)
            AutoPrint(row, result.Pages, settings);
    }

    /// <summary>Prints in the background so the modem is free for the next call immediately.</summary>
    private void AutoPrint(ReceivedFax row, IReadOnlyList<FaxBitmap> pages, AppSettings settings)
    {
        var printer = string.IsNullOrWhiteSpace(settings.Incoming.AutoPrintPrinter) ? null : settings.Incoming.AutoPrintPrinter;
        _ = Task.Run(async () =>
        {
            try
            {
                PHax.Imaging.Conversion.FaxPrinter.Print(pages, printer, row.DisplayName);
                log.LogInformation("Printed received fax {Id} ({Pages} page(s)) on {Printer}", row.Id, pages.Count, printer ?? "the default printer");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Automatic printing of fax {Id} failed", row.Id);
                await notifier.NotifyAsync(NotificationKind.FaxFailed, "Automatic printing failed",
                    $"{row.DisplayName} was received but could not be printed on {printer ?? "the default printer"}: {ex.Message}", row.Id, settings);
            }
        });
    }

    private static string Sanitize(string s)
    {
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c is '+' or '-').Take(24).ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }
}
