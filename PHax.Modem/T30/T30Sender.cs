using System.Diagnostics;
using PHax.Core.Interfaces;
using PHax.Core.Models;
using PHax.Modem.Class1;

namespace PHax.Modem.T30;

/// <summary>
/// T.30 transmitting (calling) station without ECM.
/// A fax is reported <see cref="FaxOutcome.Success"/> only when every page has been answered MCF (or RTP)
/// and the last page was signalled with EOP.
/// </summary>
public sealed class T30Sender : T30SessionBase, IFaxSender
{
    private readonly IFaxImageEncoder _encoder;
    private FaxSendResult _result = new();
    private bool _connected;
    /// <summary>True while a page has been put on the line but not yet answered.</summary>
    private bool _pageInDoubt;

    private DisInfo _dis = new();
    private List<FaxModulation> _candidates = [];
    private int _modIndex;
    private bool _fine, _mr;
    private string _localId = "";

    public T30Sender(IFaxModem modem, IProtocolLog log, T30Options options, IFaxImageEncoder encoder) : base(modem, log, options) =>
        _encoder = encoder;

    private FaxModulation Mod => _candidates[_modIndex];

    public async Task<FaxSendResult> SendAsync(FaxSendRequest request, CancellationToken ct)
    {
        _result = new FaxSendResult();
        _localId = request.LocalId;
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        try
        {
            await RunAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish(FaxOutcome.Cancelled, _pageInDoubt ? "Cancelled during page transmission; the last page may have been received." : "Cancelled.", "CANCELLED");
            if (_connected) await SendDcnAndHangupAsync(Fcf.X).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(Classify(FaxOutcome.CommunicationFailure), "Transmission timeout exceeded.", "TIMEOUT");
            if (_connected) await SendDcnAndHangupAsync(Fcf.X).ConfigureAwait(false);
        }
        catch (ModemDisconnectedException ex)
        {
            Finish(Classify(FaxOutcome.ModemDisconnected), ex.Message, "MODEM_DISCONNECTED");
        }
        catch (IOException ex)
        {
            Finish(Classify(FaxOutcome.ModemError), ex.Message, "MODEM_IO");
            await HangupQuietlyAsync().ConfigureAwait(false);
        }
        _result.Duration = sw.Elapsed;
        _result.RemoteId = RemoteId;
        _result.Modulation = CurrentModulation;
        _result.Resolution = CurrentResolution;
        Log.Log("T30", $"Send finished: {_result.Outcome} — {_result.Message} (pages {_result.PagesConfirmed}/{request.Pages.Count} confirmed)");
        return _result;
    }

    /// <summary>A failure after page data is ambiguous: classify so that nothing is retried that may already have printed.</summary>
    private FaxOutcome Classify(FaxOutcome baseOutcome)
    {
        if (_pageInDoubt) return FaxOutcome.UnconfirmedDelivery;
        if (_result.PagesConfirmed > 0) return FaxOutcome.PartialFailure;
        return baseOutcome;
    }

    private void Finish(FaxOutcome outcome, string message, string code)
    {
        _result.Outcome = outcome;
        _result.Message = message;
        _result.ErrorCode = outcome == FaxOutcome.Success ? null : code;
    }

    private async Task RunAsync(FaxSendRequest request, CancellationToken ct)
    {
        if (request.Pages.Count == 0)
        {
            Finish(FaxOutcome.PermanentFailure, "Document has no pages.", "NO_PAGES");
            return;
        }

        // Phase A: call establishment.
        Report(T30Phase.A, "Dialing");
        var dial = await Modem.DialAsync(request.DialString, request.ToneDialing, Options.DialTimeout, ct).ConfigureAwait(false);
        if (dial != DialResult.Connected)
        {
            var (outcome, msg) = dial switch
            {
                DialResult.Busy => (FaxOutcome.Busy, "Line busy."),
                DialResult.NoDialTone => (FaxOutcome.NoDialTone, "No dial tone. Check the phone line connection."),
                DialResult.NoAnswer => (FaxOutcome.NoAnswer, "No answer."),
                DialResult.NoCarrier => (FaxOutcome.NoFaxAnswer, "Call answered, but no fax machine responded."),
                _ => (FaxOutcome.ModemError, "Modem reported an error while dialing."),
            };
            Finish(outcome, msg, dial.ToString().ToUpperInvariant());
            await HangupQuietlyAsync().ConfigureAwait(false);
            return;
        }
        _connected = true;

        // Phase B: wait for DIS.
        Report(T30Phase.B, "Connected; waiting for receiver capabilities");
        DisInfo? dis = null;
        var t1 = DateTime.UtcNow + Options.T1;
        while (dis is null)
        {
            var remaining = t1 - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                Finish(FaxOutcome.NoFaxAnswer, "No DIS received from the remote station (T1 expired).", "NO_DIS");
                await SendDcnAndHangupAsync(Fcf.X).ConfigureAwait(false);
                return;
            }
            var group = await ReceiveGroupAsync(remaining, ct).ConfigureAwait(false);
            if (group.Count == 0) { await Task.Delay(100, ct).ConfigureAwait(false); continue; }
            foreach (var f in group)
            {
                if (f.Fcf == Fcf.CSI) RemoteId = StationIdentifier.Decode(f.Fif);
                else if (f.Fcf == Fcf.DIS) dis = Capabilities.ParseDis(f.Fif);
                else if (f.Fcf == Fcf.DTC)
                {
                    Finish(FaxOutcome.PermanentFailure, "Remote station requested polling (DTC); not supported.", "DTC");
                    await SendDcnAndHangupAsync(Fcf.X).ConfigureAwait(false);
                    return;
                }
                else if (f.Fcf == Fcf.DCN)
                {
                    Finish(FaxOutcome.NoFaxAnswer, "Remote station disconnected before sending capabilities.", "REMOTE_DCN");
                    await HangupQuietlyAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        _dis = dis;
        Log.Log("T30", $"Remote capabilities: {dis}{(RemoteId is { Length: > 0 } ? $", CSI \"{RemoteId}\"" : "")}");

        if (!dis.CanReceive)
        {
            Finish(FaxOutcome.RemoteRejected, "Remote station is not able to receive faxes.", "REMOTE_CANNOT_RECEIVE");
            await SendDcnAndHangupAsync(Fcf.X).ConfigureAwait(false);
            return;
        }

        int limit = Math.Min(request.MaxBitRate, Options.MaxBitRate);
        var local = Capabilities.FromModemCodes(Modem.Capabilities?.TransmitModulations ?? []);
        _candidates = dis.Modulations.Where(m => m.BitRate <= limit && local.Contains(m))
            .Where(m => request.AllowV17 || m.Family != ModulationFamily.V17)
            .OrderByDescending(m => m.BitRate)
            .ThenByDescending(m => m.Family == ModulationFamily.V17).ToList();
        if (_candidates.Count == 0)
        {
            Finish(FaxOutcome.CommunicationFailure, "No modulation in common with the remote station.", "NO_COMMON_MODULATION");
            await SendDcnAndHangupAsync(Fcf.X).ConfigureAwait(false);
            return;
        }
        _fine = request.AllowFine && dis.Fine;
        _mr = request.AllowMr && dis.TwoDimensional;
        CurrentResolution = _fine ? FaxResolution.Fine : FaxResolution.Standard;

        if (!await TrainAsync(ct).ConfigureAwait(false)) return;

        bool firstAfterTraining = true;
        int retransmissions = 0;
        int index = 0;
        int total = request.Pages.Count;
        while (index < total)
        {
            var page = _fine ? request.Pages[index].ToFine() : request.Pages[index].ToStandard();
            int minBits = Mod.BitRate * _dis.MinScanLineMs / 1000;
            var compression = _mr ? FaxCompression.MR : FaxCompression.MH;
            var data = _encoder.EncodeT4(page, compression, minBits, lsbFirst: true);
            int code = Mod.Family == ModulationFamily.V17 && Options.V17ShortTrainAfterFirstPage && !firstAfterTraining
                ? Mod.ShortTrainCode : Mod.LongTrainCode;

            // Phase C: message transmission.
            Report(T30Phase.C, $"Sending page {index + 1} of {total}", index + 1, total, 0, data.Length);
            var progress = new Progress<long>(b => Report(T30Phase.C, $"Sending page {index + 1} of {total}", index + 1, total, b, data.Length));
            _pageInDoubt = true;
            var status = await Modem.TransmitDataAsync(code, data, progress, ct).ConfigureAwait(false);
            if (status == DataTransmitStatus.NotStarted)
            {
                _pageInDoubt = false;
                Finish(Classify(FaxOutcome.CommunicationFailure), $"Modem refused to start the {Mod.Name} carrier for page {index + 1}.", "FTM_REFUSED");
                await SendDcnAndHangupAsync(Fcf.X).ConfigureAwait(false);
                return;
            }
            _result.PageDataTransmitted = true;
            _result.PagesSent = Math.Max(_result.PagesSent, index + 1);
            if (status == DataTransmitStatus.Aborted)
            {
                Finish(Classify(FaxOutcome.CommunicationFailure), $"Page {index + 1} transmission was interrupted.", "FTM_ABORTED");
                await SendDcnAndHangupAsync(Fcf.X).ConfigureAwait(false);
                return;
            }
            firstAfterTraining = false;

            // Phase D: post-message procedure.
            bool last = index == total - 1;
            byte post = last ? Fcf.EOP : Fcf.MPS;
            Report(T30Phase.D, $"Waiting for confirmation of page {index + 1}", index + 1, total);
            await Modem.TransmitSilenceAsync(Options.SilenceAfterPageData, ct).ConfigureAwait(false);
            T30Frame? response = null;
            for (int attempt = 1; attempt <= Options.CommandAttempts && response is null; attempt++)
            {
                if (attempt > 1) Log.Log("T30", $"No response to {Fcf.Name(post)}; repeating ({attempt}/{Options.CommandAttempts}).");
                if (!await SendFramesAsync(ct, T30Frame.Build((byte)(post | Fcf.X), [], final: true)).ConfigureAwait(false)) continue;
                var group = await ReceiveGroupAsync(Options.T4, ct).ConfigureAwait(false);
                response = group.LastOrDefault(f => f.Fcf is Fcf.MCF or Fcf.RTP or Fcf.RTN or Fcf.PIP or Fcf.PIN or Fcf.DCN);
            }

            if (response is null)
            {
                Finish(FaxOutcome.UnconfirmedDelivery,
                    $"Page {index + 1} was sent but the receiver did not confirm it after {Options.CommandAttempts} attempts.", "NO_MCF");
                await SendDcnAndHangupAsync(Fcf.X).ConfigureAwait(false);
                return;
            }

            switch (response.Fcf)
            {
                case Fcf.MCF:
                case Fcf.PIP:
                    ConfirmPage();
                    index++;
                    retransmissions = 0;
                    break;
                case Fcf.RTP:
                    ConfirmPage();
                    index++;
                    retransmissions = 0;
                    if (index < total)
                    {
                        if (!await TrainAsync(ct).ConfigureAwait(false)) return;
                        firstAfterTraining = true;
                    }
                    break;
                case Fcf.RTN:
                case Fcf.PIN:
                    // The receiver rejected the page: it is not counted as delivered.
                    _pageInDoubt = false;
                    if (++retransmissions > Options.PageRetransmissions)
                    {
                        Finish(Classify(FaxOutcome.CommunicationFailure),
                            $"Page {index + 1} was rejected by the receiver ({response.Name}) {retransmissions} times.", "PAGE_REJECTED");
                        await SendDcnAndHangupAsync(Fcf.X).ConfigureAwait(false);
                        return;
                    }
                    StepDownAfterRejection(retransmissions);
                    Log.Log("T30", $"Page {index + 1} rejected ({response.Name}); retransmitting at {Mod.Name} with {(_mr ? "MR" : "MH")} coding.");
                    if (!await TrainAsync(ct).ConfigureAwait(false)) return;
                    firstAfterTraining = true;
                    break;
                case Fcf.DCN:
                    Finish(Classify(FaxOutcome.CommunicationFailure), $"Receiver disconnected before confirming page {index + 1}.", "REMOTE_DCN");
                    await HangupQuietlyAsync().ConfigureAwait(false);
                    return;
            }
        }

        _result.DeliveryConfirmed = true;
        Finish(FaxOutcome.Success, $"{total} page(s) delivered and confirmed.", "");
        Report(T30Phase.E, "Delivery confirmed; disconnecting", total, total);
        await SendDcnAndHangupAsync(Fcf.X).ConfigureAwait(false);
    }

    /// <summary>After RTN/PIN: switch to MH (a line error in MR corrupts the following 2D lines too) and fall back.</summary>
    private void StepDownAfterRejection(int rejections)
    {
        _mr = false;
        _modIndex = NextFallbackIndex();
    }

    /// <summary>
    /// V.17 is tried only once: any V.17 failure (FTT or RTN) jumps straight to the best V.29/V.27ter rate, because on
    /// VoIP and digital lines V.17 tends to fail at every V.17 rate. Below V.17 the speed steps down one rate at a time.
    /// </summary>
    private int NextFallbackIndex()
    {
        if (Mod.Family == ModulationFamily.V17 && _candidates.Any(m => m.Family != ModulationFamily.V17))
        {
            // Drop V.17 for the rest of the call (V.17 7200 sorts below V.29 9600, so it must be removed, not skipped).
            _candidates = _candidates.Where(m => m.Family != ModulationFamily.V17).ToList();
            return 0;
        }
        return Math.Min(_modIndex + 1, _candidates.Count - 1);
    }

    private void ConfirmPage()
    {
        _pageInDoubt = false;
        _result.PagesConfirmed++;
        Log.Log("T30", $"Page {_result.PagesConfirmed} confirmed.");
    }

    /// <summary>Sends DCS + TCF until CFR, falling back to slower modulations after FTT. On failure sets the result and disconnects.</summary>
    private async Task<bool> TrainAsync(CancellationToken ct)
    {
        while (_modIndex < _candidates.Count)
        {
            CurrentModulation = Mod.Name;
            bool ftt = false;
            for (int attempt = 1; attempt <= Options.CommandAttempts; attempt++)
            {
                Report(T30Phase.B, $"Training at {Mod.Name}{(attempt > 1 ? $" (attempt {attempt})" : "")}");
                var frames = new List<byte[]>();
                if (!string.IsNullOrWhiteSpace(_localId))
                    frames.Add(T30Frame.Build(Fcf.TSI | Fcf.X, StationIdentifier.Encode(_localId), final: false));
                frames.Add(T30Frame.Build(Fcf.DCS | Fcf.X, Capabilities.BuildDcs(Mod, _fine, _mr, _dis.MinScanLineMs), final: true));
                Log.Log("T30", $"DCS: {Mod.Name} {(_fine ? "fine" : "standard")} {(_mr ? "MR" : "MH")} minScan={_dis.MinScanLineMs}ms");
                if (!await SendFramesAsync(ct, [.. frames]).ConfigureAwait(false)) continue;

                await Modem.TransmitSilenceAsync(Options.SilenceBeforeTcf, ct).ConfigureAwait(false);
                var tcf = new byte[(int)(Mod.BytesPerSecond * Options.TcfSeconds)];
                var status = await Modem.TransmitDataAsync(Mod.LongTrainCode, tcf, null, ct).ConfigureAwait(false);
                if (status != DataTransmitStatus.Completed)
                {
                    Log.Log("T30", $"TCF not transmitted ({status}).");
                    continue;
                }

                var group = await ReceiveGroupAsync(Options.T4, ct).ConfigureAwait(false);
                if (group.Any(f => f.Fcf == Fcf.CFR))
                {
                    Log.Log("T30", $"Training accepted at {Mod.Name}.");
                    return true;
                }
                if (group.Any(f => f.Fcf == Fcf.FTT))
                {
                    Log.Log("T30", $"Training failed at {Mod.Name} (FTT).");
                    ftt = true;
                    break;
                }
                if (group.Any(f => f.Fcf == Fcf.DCN))
                {
                    Finish(Classify(FaxOutcome.CommunicationFailure), "Receiver disconnected during training.", "REMOTE_DCN");
                    await HangupQuietlyAsync().ConfigureAwait(false);
                    return false;
                }
                // No response, or DIS repeated (our DCS was not received): repeat.
            }
            if (!ftt)
            {
                Finish(Classify(FaxOutcome.CommunicationFailure), $"No response to DCS after {Options.CommandAttempts} attempts.", "NO_CFR");
                await SendDcnAndHangupAsync(Fcf.X).ConfigureAwait(false);
                return false;
            }
            if (_modIndex >= _candidates.Count - 1) break;
            _modIndex = NextFallbackIndex();
        }
        _modIndex = _candidates.Count - 1;
        Finish(Classify(FaxOutcome.TrainingFailed), "Training failed at every available speed.", "TRAINING_FAILED");
        await SendDcnAndHangupAsync(Fcf.X).ConfigureAwait(false);
        return false;
    }
}
