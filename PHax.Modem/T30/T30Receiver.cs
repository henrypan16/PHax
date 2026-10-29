using System.Diagnostics;
using PHax.Core.Interfaces;
using PHax.Core.Models;
using PHax.Modem.Class1;

namespace PHax.Modem.T30;

/// <summary>T.30 receiving (answering) station without ECM.</summary>
public sealed class T30Receiver : T30SessionBase, IFaxReceiver
{
    private enum State { SendDis, WaitCommand, ReceivePage, Done }

    private readonly IFaxImageDecoder _decoder;
    private FaxReceiveResult _result = new();

    public T30Receiver(IFaxModem modem, IProtocolLog log, T30Options options, IFaxImageDecoder decoder) : base(modem, log, options) =>
        _decoder = decoder;

    public static bool IsTcfGood(ReadOnlySpan<byte> data, FaxModulation modulation, double minZeroRunSeconds)
    {
        int needed = (int)(modulation.BytesPerSecond * minZeroRunSeconds);
        int run = 0, best = 0;
        foreach (var b in data)
        {
            if (b == 0) { if (++run > best) best = run; }
            else run = 0;
        }
        return best >= needed;
    }

    public async Task<FaxReceiveResult> ReceiveAsync(FaxReceiveRequest request, CancellationToken ct)
    {
        _result = new FaxReceiveResult();
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        try
        {
            await RunAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish(FaxOutcome.Cancelled, "Reception cancelled.", "CANCELLED");
            await SendDcnAndHangupAsync(0).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(FaxOutcome.CommunicationFailure, "Reception timeout exceeded.", "TIMEOUT");
            await SendDcnAndHangupAsync(0).ConfigureAwait(false);
        }
        catch (ModemDisconnectedException ex)
        {
            Finish(FaxOutcome.ModemDisconnected, ex.Message, "MODEM_DISCONNECTED");
        }
        catch (IOException ex)
        {
            Finish(FaxOutcome.ModemError, ex.Message, "MODEM_IO");
            await HangupQuietlyAsync().ConfigureAwait(false);
        }
        _result.Duration = sw.Elapsed;
        _result.RemoteId = RemoteId;
        _result.Modulation = CurrentModulation;
        _result.Resolution = CurrentResolution;
        Log.Log("T30", $"Receive finished: {_result.Outcome} — {_result.Message} ({_result.Pages.Count} page(s))");
        return _result;
    }

    private void Finish(FaxOutcome outcome, string message, string code)
    {
        // Pages already confirmed are kept whatever happens afterwards.
        if (outcome != FaxOutcome.Success && _result.Pages.Count > 0 && outcome is not FaxOutcome.Cancelled)
            outcome = FaxOutcome.PartialFailure;
        _result.Outcome = outcome;
        _result.Message = message;
        _result.ErrorCode = outcome == FaxOutcome.Success ? null : code;
    }

    private async Task<bool> RespondAsync(byte fcf, CancellationToken ct)
    {
        if (Options.WaitForSilenceBeforeResponse) await Modem.WaitForSilenceAsync(Options.ResponseSilence, ct).ConfigureAwait(false);
        return await SendFramesAsync(ct, T30Frame.Build(fcf, [], final: true)).ConfigureAwait(false);
    }

    private async Task RunAsync(FaxReceiveRequest request, CancellationToken ct)
    {
        if (!request.AlreadyAnswered)
        {
            Report(T30Phase.A, "Answering");
            if (!await Modem.AnswerAsync(Options.AnswerTimeout, ct).ConfigureAwait(false))
            {
                Finish(FaxOutcome.NoFaxAnswer, "Answer failed: no fax connection was established.", "ANSWER_FAILED");
                await HangupQuietlyAsync().ConfigureAwait(false);
                return;
            }
        }

        int limit = Math.Min(request.MaxBitRate, Options.MaxBitRate);
        var receivable = Capabilities.FromModemCodes(Modem.Capabilities?.ReceiveModulations ?? [])
            .Where(m => m.BitRate <= limit && (request.AllowV17 || m.Family != ModulationFamily.V17)).ToList();
        if (receivable.Count == 0) receivable.Add(FaxModulation.V27_2400);
        var disFrames = new[]
        {
            T30Frame.Build(Fcf.CSI, StationIdentifier.Encode(request.LocalId), final: false),
            T30Frame.Build(Fcf.DIS, Capabilities.BuildDis(receivable, request.AllowFine, request.AllowMr), final: true),
        };

        var t1 = DateTime.UtcNow + Options.T1;
        var state = State.SendDis;
        List<T30Frame>? pending = null;
        DcsInfo? dcs = null;
        bool trained = false, firstPageAfterTcf = true, eopConfirmed = false;
        byte? lastResponse = null;
        int silentWaits = 0;

        while (state != State.Done)
        {
            ct.ThrowIfCancellationRequested();
            switch (state)
            {
                case State.SendDis:
                    if (DateTime.UtcNow > t1)
                    {
                        Finish(FaxOutcome.NoFaxAnswer, "No DCS received from the calling station (T1 expired).", "NO_DCS");
                        await SendDcnAndHangupAsync(0).ConfigureAwait(false);
                        return;
                    }
                    Report(T30Phase.B, "Sending capabilities (DIS)");
                    await SendFramesAsync(ct, disFrames).ConfigureAwait(false);
                    state = State.WaitCommand;
                    break;

                case State.WaitCommand:
                {
                    var group = pending ?? await ReceiveGroupAsync(trained ? Options.T2 : Options.T4, ct).ConfigureAwait(false);
                    pending = null;
                    if (group.Count == 0)
                    {
                        if (eopConfirmed)
                        {
                            // The document ended with EOP answered by MCF, so it is complete. The DCN that should
                            // follow is often lost or garbled as the sender drops the line; it does not affect delivery.
                            Log.Log("T30", "No valid DCN after EOP/MCF (missing or bad FCS); document is complete.");
                            _result.Complete = true;
                            Finish(FaxOutcome.Success, $"{_result.Pages.Count} page(s) received.", "");
                            Report(T30Phase.E, "Document complete");
                            await HangupQuietlyAsync().ConfigureAwait(false);
                            return;
                        }
                        if (!trained) { state = State.SendDis; break; }
                        if (++silentWaits >= Options.CommandAttempts)
                        {
                            Finish(FaxOutcome.CommunicationFailure, "Calling station stopped responding.", "NO_COMMAND");
                            await SendDcnAndHangupAsync(0).ConfigureAwait(false);
                            return;
                        }
                        break;
                    }
                    silentWaits = 0;

                    foreach (var f in group.Where(f => f.Fcf == Fcf.TSI)) RemoteId = StationIdentifier.Decode(f.Fif);
                    var command = group.LastOrDefault(f => f.Fcf is Fcf.DCS or Fcf.DCN or Fcf.MPS or Fcf.EOP or Fcf.EOM
                        or Fcf.PRI_MPS or Fcf.PRI_EOP or Fcf.PRI_EOM or Fcf.CRP);
                    if (command is null) break;

                    if (command.Fcf == Fcf.DCN)
                    {
                        if (eopConfirmed)
                        {
                            _result.Complete = true;
                            Finish(FaxOutcome.Success, $"{_result.Pages.Count} page(s) received.", "");
                        }
                        else
                        {
                            Finish(FaxOutcome.CommunicationFailure, "Calling station disconnected before the end of the document.", "REMOTE_DCN");
                        }
                        Report(T30Phase.E, "Remote disconnected");
                        await HangupQuietlyAsync().ConfigureAwait(false);
                        return;
                    }

                    if (command.Fcf == Fcf.CRP)
                    {
                        // Repeat request: resend our last response, or DIS before training.
                        if (lastResponse is { } lr) await RespondAsync(lr, ct).ConfigureAwait(false);
                        else state = State.SendDis;
                        break;
                    }

                    if (command.Fcf is Fcf.MPS or Fcf.EOP or Fcf.EOM or Fcf.PRI_MPS or Fcf.PRI_EOP or Fcf.PRI_EOM)
                    {
                        // Our response to a post-message command was lost and the sender repeated it.
                        if (lastResponse is { } lr)
                        {
                            Log.Log("T30", $"Repeated {command.Name}; resending {Fcf.Name(lr)}.");
                            await RespondAsync(lr, ct).ConfigureAwait(false);
                            if (lr == Fcf.MCF && command.Fcf is Fcf.MPS or Fcf.PRI_MPS) state = State.ReceivePage;
                            else if (lr == Fcf.MCF && command.Fcf is Fcf.EOM or Fcf.PRI_EOM) { trained = false; state = State.SendDis; }
                        }
                        else
                        {
                            await RespondAsync(Fcf.RTN, ct).ConfigureAwait(false);
                            lastResponse = Fcf.RTN;
                        }
                        break;
                    }

                    // DCS: parse, then receive TCF.
                    dcs = Capabilities.ParseDcs(command.Fif);
                    if (dcs is null || dcs.Width != FaxBitmap.A4Width)
                    {
                        Log.Log("T30", dcs is null ? "DCS with unsupported rate." : $"DCS requests unsupported width {dcs.Width}.");
                        await RespondAsync(Fcf.FTT, ct).ConfigureAwait(false);
                        lastResponse = null;
                        break;
                    }
                    Log.Log("T30", $"DCS: {dcs}{(RemoteId is { Length: > 0 } ? $", TSI \"{RemoteId}\"" : "")}");
                    CurrentModulation = dcs.Modulation.Name;
                    CurrentResolution = dcs.Resolution;
                    _result.Resolution = dcs.Resolution;
                    Report(T30Phase.B, $"Receiving training check at {dcs.Modulation.Name}");

                    var tcf = await Modem.ReceiveDataAsync(dcs.Modulation.LongTrainCode, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
                    if (tcf.Status == DataStatus.WrongCarrier)
                    {
                        Log.Log("T30", "Expected TCF, received V.21; waiting for the next command.");
                        break;
                    }
                    if (tcf.Status != DataStatus.Ok)
                    {
                        Log.Log("T30", $"No TCF received ({tcf.Status}).");
                        break;
                    }
                    if (!IsTcfGood(tcf.Data, dcs.Modulation, Options.TcfMinZeroRunSeconds))
                    {
                        Log.Log("T30", $"TCF rejected at {dcs.Modulation.Name} ({tcf.Data.Length} bytes).");
                        await RespondAsync(Fcf.FTT, ct).ConfigureAwait(false);
                        lastResponse = null;
                        trained = false;
                        break;
                    }
                    await RespondAsync(Fcf.CFR, ct).ConfigureAwait(false);
                    lastResponse = null;
                    trained = true;
                    firstPageAfterTcf = true;
                    state = State.ReceivePage;
                    break;
                }

                case State.ReceivePage:
                {
                    var mod = dcs!.Modulation;
                    int code = mod.Family == ModulationFamily.V17 && Options.V17ShortTrainAfterFirstPage && !firstPageAfterTcf
                        ? mod.ShortTrainCode : mod.LongTrainCode;
                    int pageNo = _result.Pages.Count + 1;
                    Report(T30Phase.C, $"Receiving page {pageNo}", pageNo);
                    var data = await Modem.ReceiveDataAsync(code, Options.T2, request.Timeout, ct).ConfigureAwait(false);
                    if (data.Status != DataStatus.Ok && data.Data.Length == 0)
                    {
                        // V.21 instead of page carrier: DCS repeated (our CFR was lost) or a repeated post-message command.
                        Log.Log("T30", $"No page carrier ({data.Status}); listening for commands.");
                        state = State.WaitCommand;
                        break;
                    }
                    firstPageAfterTcf = false;

                    var decoded = _decoder.DecodeT4(data.Data, dcs.Width, dcs.Resolution, dcs.Compression, lsbFirst: true);
                    int lines = decoded.GoodLines + decoded.BadLines;
                    double badRatio = lines == 0 ? 1 : (double)decoded.BadLines / lines;
                    Log.Log("T30", $"Page {pageNo}: {data.Data.Length} bytes, {decoded.GoodLines} good / {decoded.BadLines} bad lines, RTC={(decoded.RtcFound ? "yes" : "no")}");

                    // Phase D: post-message command.
                    Report(T30Phase.D, $"Page {pageNo} received; waiting for end-of-page signal", pageNo);
                    T30Frame? post = null;
                    List<T30Frame> group = [];
                    for (int attempt = 0; attempt < Options.CommandAttempts && post is null; attempt++)
                    {
                        group = await ReceiveGroupAsync(Options.T2, ct).ConfigureAwait(false);
                        post = group.LastOrDefault(f => f.Fcf is Fcf.MPS or Fcf.EOP or Fcf.EOM or Fcf.PRI_MPS or Fcf.PRI_EOP or Fcf.PRI_EOM);
                        if (post is null && group.Any(f => f.Fcf is Fcf.DCS or Fcf.DCN)) break;
                    }
                    if (post is null)
                    {
                        if (group.Count > 0) { pending = group; state = State.WaitCommand; break; }
                        Finish(FaxOutcome.CommunicationFailure, $"No end-of-page signal after page {pageNo}.", "NO_POST_MESSAGE");
                        await SendDcnAndHangupAsync(0).ConfigureAwait(false);
                        return;
                    }

                    byte response;
                    if (lines == 0 || badRatio > Options.RtpMaxBadLineRatio || decoded.MaxConsecutiveBadLines > Options.MaxConsecutiveBadLines)
                        response = Fcf.RTN;
                    else if (badRatio > Options.McfMaxBadLineRatio)
                        response = Fcf.RTP;
                    else
                        response = Fcf.MCF;

                    if (response != Fcf.RTN)
                    {
                        _result.Pages.Add(decoded.Page);
                        request.PageReceived?.Invoke(decoded.Page, _result.Pages.Count);
                    }
                    await RespondAsync(response, ct).ConfigureAwait(false);
                    lastResponse = response;
                    Log.Log("T30", $"Page {pageNo}: answered {Fcf.Name(response)} to {post.Name}.");

                    bool isEop = post.Fcf is Fcf.EOP or Fcf.PRI_EOP;
                    bool isEom = post.Fcf is Fcf.EOM or Fcf.PRI_EOM;
                    if (isEop && response != Fcf.RTN) eopConfirmed = true;

                    if (response == Fcf.MCF && post.Fcf is Fcf.MPS or Fcf.PRI_MPS)
                        state = State.ReceivePage;
                    else if (isEom && response != Fcf.RTN)
                    {
                        trained = false;
                        state = State.SendDis;
                    }
                    else
                    {
                        // EOP (expect DCN), RTP/RTN (expect DCS for retraining, or DCN).
                        state = State.WaitCommand;
                    }
                    break;
                }
            }
        }
    }
}
