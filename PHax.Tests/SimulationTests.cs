using PHax.Core.Models;
using PHax.Core.Services;
using PHax.Imaging.Decoding;
using PHax.Imaging.Encoding;
using PHax.Modem.Class1;
using PHax.Modem.Profiles;
using PHax.Modem.Simulation;
using PHax.Modem.T30;
using Xunit;

namespace PHax.Tests;

/// <summary>
/// End-to-end T.30 sessions between PHax's sender and receiver over two simulated Class 1 modems.
/// These are SIMULATION tests: they verify protocol logic and failure handling, not hardware compatibility.
/// </summary>
[Trait("Category", "Simulation")]
public sealed class SimulationTests : IAsyncLifetime
{
    private const string ReceiverNumber = "5550100";
    private readonly SimulatedLine _line = new();
    private readonly ProtocolLog _log = new();
    private SimulatedModemTransport _txPort = null!, _rxPort = null!;
    private Class1Modem _tx = null!, _rx = null!;

    private static readonly T30Options FastOptions = new()
    {
        T1 = TimeSpan.FromSeconds(10),
        T2 = TimeSpan.FromSeconds(2),
        T4 = TimeSpan.FromSeconds(1),
    };

    public async ValueTask InitializeAsync()
    {
        _txPort = new SimulatedModemTransport(_line, "SIM-TX", new SimulatedModemOptions { CallerNumber = "5550199" });
        _rxPort = new SimulatedModemTransport(_line, "SIM-RX");
        _line.Register(ReceiverNumber, _rxPort);
        await _txPort.OpenAsync(default);
        await _rxPort.OpenAsync(default);
        _tx = new Class1Modem(_txPort, ModemProfiles.GenericClass1, _log);
        _rx = new Class1Modem(_rxPort, ModemProfiles.GenericClass1, _log);
        await _tx.InitializeAsync(default);
        await _rx.InitializeAsync(default);
    }

    public async ValueTask DisposeAsync()
    {
        var dump = Environment.GetEnvironmentVariable("PHAX_SIM_LOG");
        if (!string.IsNullOrEmpty(dump))
            await File.WriteAllLinesAsync(dump, _log.Snapshot().Select(e => $"{e.Utc:HH:mm:ss.fff} [{e.Category}] {e.Message}"));
        await _tx.DisposeAsync();
        await _rx.DisposeAsync();
    }

    private Class1FaxEngine Engine(T30Options? options = null) =>
        new(new T4Encoder(), new T4Decoder(), _log, options ?? FastOptions);

    private async Task<(FaxSendResult Send, FaxReceiveResult? Receive)> RunSession(
        IReadOnlyList<FaxBitmap> pages, bool answer = true, T30Options? options = null, int maxBitRate = 14400,
        Action<CancellationTokenSource>? onStart = null, bool allowMr = true)
    {
        var engine = Engine(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var receiveTask = answer ? Task.Run(async () =>
        {
            ModemUnsolicitedEvent? e;
            do e = await _rx.WaitForUnsolicitedAsync(TimeSpan.FromSeconds(30), cts.Token);
            while (e is not null && e.Kind != "RING");
            if (e is null) return null;
            return await engine.CreateReceiver(_rx).ReceiveAsync(new FaxReceiveRequest { LocalId = "+1 555 0100" }, cts.Token);
        }) : Task.FromResult<FaxReceiveResult?>(null);

        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        onStart?.Invoke(sendCts);
        var send = await engine.CreateSender(_tx).SendAsync(new FaxSendRequest
        {
            DialString = ReceiverNumber,
            Pages = pages,
            LocalId = "+1 555 0199",
            MaxBitRate = maxBitRate,
            AllowMr = allowMr,
        }, sendCts.Token);
        var receive = await receiveTask.WaitAsync(TimeSpan.FromSeconds(60));
        return (send, receive);
    }

    [Fact]
    public async Task Negotiation_SelectsBestCommonParameters()
    {
        var (send, receive) = await RunSession([TestPages.Text()]);
        Assert.Equal(FaxOutcome.Success, send.Outcome);
        Assert.Equal("V.17 14400", send.Modulation);
        Assert.Equal(FaxResolution.Fine, send.Resolution);
        Assert.Equal("+1 555 0100", send.RemoteId);
        Assert.Equal("+1 555 0199", receive!.RemoteId);
    }

    [Fact]
    public async Task SinglePage_IsDeliveredAndConfirmed()
    {
        var page = TestPages.Text();
        var (send, receive) = await RunSession([page]);
        Assert.Equal(FaxOutcome.Success, send.Outcome);
        Assert.True(send.DeliveryConfirmed);
        Assert.Equal(1, send.PagesConfirmed);
        Assert.NotNull(receive);
        Assert.True(receive.Complete);
        Assert.Equal(FaxOutcome.Success, receive.Outcome);
        Assert.Single(receive.Pages);
        Assert.True(page.ContentEquals(receive.Pages[0]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultiPage_IsDeliveredInOrder(bool allowMr)
    {
        var pages = new[] { TestPages.Text("one", 1), TestPages.Patterns(), TestPages.Text("three", 3) };
        var (send, receive) = await RunSession(pages, allowMr: allowMr);
        Assert.Equal(FaxOutcome.Success, send.Outcome);
        Assert.Equal(3, send.PagesConfirmed);
        Assert.Equal(3, receive!.Pages.Count);
        for (int i = 0; i < 3; i++) Assert.True(pages[i].ContentEquals(receive.Pages[i]), $"page {i + 1}");
    }

    [Fact]
    public async Task StandardResolutionPage_IsSentAtStandard_WhenFineDisabledByReceiver()
    {
        var engine = Engine();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var rxTask = Task.Run(async () =>
        {
            while ((await _rx.WaitForUnsolicitedAsync(TimeSpan.FromSeconds(30), cts.Token))?.Kind != "RING") { }
            return await engine.CreateReceiver(_rx).ReceiveAsync(new FaxReceiveRequest { AllowFine = false }, cts.Token);
        });
        var page = TestPages.Text();
        var send = await engine.CreateSender(_tx).SendAsync(new FaxSendRequest { DialString = ReceiverNumber, Pages = [page] }, cts.Token);
        var receive = await rxTask;
        Assert.Equal(FaxOutcome.Success, send.Outcome);
        Assert.Equal(FaxResolution.Standard, send.Resolution);
        Assert.True(page.ToStandard().ContentEquals(receive.Pages[0]));
    }

    [Fact]
    public async Task BusyLine_IsReportedAsBusy_AndRetried()
    {
        _line.Faults.DialResult = SimulatedDialResult.Busy;
        var (send, _) = await RunSession([TestPages.Text()], answer: false);
        Assert.Equal(FaxOutcome.Busy, send.Outcome);
        Assert.False(send.PageDataTransmitted);
        Assert.Equal(OutboundStatus.RetryWaiting, RetryPolicy.Decide(send.Outcome, 1, 3, new RedialSettings(), DateTime.UtcNow).Status);
    }

    [Fact]
    public async Task NoAnswer_IsReported()
    {
        _line.Faults.DialResult = SimulatedDialResult.NoAnswer;
        var (send, _) = await RunSession([TestPages.Text()], answer: false);
        Assert.Equal(FaxOutcome.NoAnswer, send.Outcome);
    }

    [Fact]
    public async Task UnansweredCall_IsReportedAsNoFaxAnswer()
    {
        // Callee rings but nobody answers within the simulated modem's wait.
        await _txPort.DisposeAsync();
        await _tx.DisposeAsync();
        _txPort = new SimulatedModemTransport(_line, "SIM-TX2", new SimulatedModemOptions { AnswerWait = TimeSpan.FromSeconds(1) });
        await _txPort.OpenAsync(default);
        _tx = new Class1Modem(_txPort, ModemProfiles.GenericClass1, _log);
        await _tx.InitializeAsync(default);
        var (send, _) = await RunSession([TestPages.Text()], answer: false);
        Assert.Equal(FaxOutcome.NoFaxAnswer, send.Outcome);
    }

    [Fact]
    public async Task FailedTraining_FallsBackToLowerSpeed()
    {
        _line.Faults.MaxCleanBitRate = 9600;
        var (send, receive) = await RunSession([TestPages.Text()]);
        Assert.Equal(FaxOutcome.Success, send.Outcome);
        // V.17 is tried once (14400); after FTT the sender goes straight to V.29.
        Assert.Equal("V.29 9600", send.Modulation);
        // Sender-side DCS lines only (the receiver's include "width=").
        Assert.Equal(2, _log.Snapshot().Count(e => e.Message.StartsWith("DCS:") && !e.Message.Contains("width")));
        Assert.Single(receive!.Pages);
    }

    [Fact]
    public async Task FailedTraining_AtAllSpeeds_FailsWithoutSendingPages()
    {
        _line.Faults.MaxCleanBitRate = 0;
        var (send, receive) = await RunSession([TestPages.Text()]);
        Assert.Equal(FaxOutcome.TrainingFailed, send.Outcome);
        // V.17 14400 once, then V.29 9600, V.29 7200, V.27ter 4800, V.27ter 2400.
        Assert.Equal(["V.17 14400", "V.29 9600", "V.29 7200", "V.27ter 4800", "V.27ter 2400"],
            _log.Snapshot().Where(e => e.Message.StartsWith("Training failed at ")).Select(e => e.Message[19..^7]).ToList());
        Assert.False(send.PageDataTransmitted);
        Assert.Empty(receive!.Pages);
    }

    [Fact]
    public async Task MissingAcknowledgement_IsUncertain_AndNotRetried()
    {
        _line.Faults.OnHdlc = c => c.Sender == "SIM-RX" && c.Fcf == Fcf.MCF ? FaultAction.Drop : FaultAction.Deliver;
        var (send, _) = await RunSession([TestPages.Text()]);
        Assert.Equal(FaxOutcome.UnconfirmedDelivery, send.Outcome);
        Assert.True(send.PageDataTransmitted);
        Assert.False(send.DeliveryConfirmed);
        Assert.Equal(OutboundStatus.Uncertain, RetryPolicy.Decide(send.Outcome, 1, 3, new RedialSettings(), DateTime.UtcNow).Status);
    }

    [Fact]
    public async Task LostMcf_IsRecoveredByRepeatingMps_WithoutDuplicatePage()
    {
        _line.Faults.OnHdlc = c => c.Sender == "SIM-RX" && c.Fcf == Fcf.MCF && c.Occurrence == 1 ? FaultAction.Drop : FaultAction.Deliver;
        var pages = new[] { TestPages.Text("a", 1), TestPages.Text("b", 2) };
        var (send, receive) = await RunSession(pages);
        Assert.Equal(FaxOutcome.Success, send.Outcome);
        Assert.Equal(2, receive!.Pages.Count);
    }

    /// <summary>Seen on a real CX93001: the sender's final DCN arrived with a bad FCS. EOP + MCF already completed the document.</summary>
    [Theory]
    [InlineData(FaultAction.Corrupt)]
    [InlineData(FaultAction.Drop)]
    public async Task GarbledOrMissingDcnAfterEop_ReceptionIsComplete(FaultAction action)
    {
        _line.Faults.OnHdlc = c => c.Sender == "SIM-TX" && c.Fcf == Fcf.DCN ? action : FaultAction.Deliver;
        var (send, receive) = await RunSession([TestPages.Text(), TestPages.Text("2", 2)]);
        Assert.Equal(FaxOutcome.Success, send.Outcome);
        Assert.Equal(FaxOutcome.Success, receive!.Outcome);
        Assert.True(receive.Complete);
        Assert.Equal(2, receive.Pages.Count);
    }

    /// <summary>
    /// Seen between two CX93001 modems on VoIP lines: V.17 TCF passes at every rate but V.17 page data arrives as noise,
    /// so every page is rejected (RTN). The sender must leave V.17 and drop MR rather than give up.
    /// </summary>
    [Fact]
    public async Task V17PagesGarbledOnLine_FallsBackToV29AndMh()
    {
        int[] v17 = [73, 74, 97, 98, 121, 122, 145, 146];
        _line.Faults.OnData = c =>
        {
            if (c.Sender != "SIM-TX" || c.Data.Length <= 3000 || !v17.Contains(c.Modulation)) return c.Data;
            var noise = new byte[c.Data.Length];
            new Random(c.Occurrence).NextBytes(noise);
            return noise;
        };
        var page = TestPages.Text();
        var (send, receive) = await RunSession([page]);
        Assert.Equal(FaxOutcome.Success, send.Outcome);
        Assert.Equal("V.29 9600", send.Modulation);
        Assert.True(page.ContentEquals(Assert.Single(receive!.Pages)));
        // Exactly one V.17 attempt, then V.29 with MH.
        var dcs = _log.Snapshot().Where(e => e.Message.StartsWith("DCS:") && e.Message.Contains(" fine ") && !e.Message.Contains("width")).Select(e => e.Message).ToList();
        Assert.Equal(2, dcs.Count);
        Assert.StartsWith("DCS: V.17 14400", dcs[0]);
        Assert.StartsWith("DCS: V.29 9600 fine MH", dcs[1]);
    }

    [Fact]
    public async Task CompatibilityMode_ReceiverWithoutV17AndMr_IsHonoured()
    {
        var engine = Engine();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var rxTask = Task.Run(async () =>
        {
            while ((await _rx.WaitForUnsolicitedAsync(TimeSpan.FromSeconds(30), cts.Token))?.Kind != "RING") { }
            return await engine.CreateReceiver(_rx).ReceiveAsync(new FaxReceiveRequest { AllowV17 = false, AllowMr = false }, cts.Token);
        });
        var page = TestPages.Text();
        var send = await engine.CreateSender(_tx).SendAsync(new FaxSendRequest { DialString = ReceiverNumber, Pages = [page], AllowV17 = false }, cts.Token);
        var receive = await rxTask;
        Assert.Equal(FaxOutcome.Success, send.Outcome);
        Assert.Equal("V.29 9600", send.Modulation);
        Assert.True(page.ContentEquals(receive.Pages[0]));
        Assert.Contains("MH", _log.Snapshot().Last(e => e.Message.StartsWith("DCS:")).Message);
    }

    [Fact]
    public async Task CorruptedDcs_IsRepeated()
    {
        _line.Faults.OnHdlc = c => c.Sender == "SIM-TX" && c.Fcf == Fcf.DCS && c.Occurrence == 1 ? FaultAction.Corrupt : FaultAction.Deliver;
        var (send, receive) = await RunSession([TestPages.Text()]);
        Assert.Equal(FaxOutcome.Success, send.Outcome);
        Assert.Single(receive!.Pages);
    }

    [Fact]
    public async Task LostCfr_IsRecovered()
    {
        _line.Faults.OnHdlc = c => c.Sender == "SIM-RX" && c.Fcf == Fcf.CFR && c.Occurrence == 1 ? FaultAction.Drop : FaultAction.Deliver;
        var (send, receive) = await RunSession([TestPages.Text()]);
        Assert.Equal(FaxOutcome.Success, send.Outcome);
        Assert.Single(receive!.Pages);
    }

    [Fact]
    public async Task CorruptedPage_IsRejectedAndRetransmitted()
    {
        int pageCarriers = 0;
        _line.Faults.OnData = c =>
        {
            // Pages are much larger than TCF; corrupt the first page carrier only.
            if (c.Sender == "SIM-TX" && c.Data.Length > 3000 && ++pageCarriers == 1)
            {
                var noisy = (byte[])c.Data.Clone();
                var rnd = new Random(5);
                for (int i = 100; i < noisy.Length - 100; i += 7) noisy[i] = (byte)rnd.Next(256);
                return noisy;
            }
            return c.Data;
        };
        var page = TestPages.Text();
        var (send, receive) = await RunSession([page]);
        Assert.Equal(FaxOutcome.Success, send.Outcome);
        Assert.Equal(2, pageCarriers);
        Assert.Single(receive!.Pages);
        Assert.True(page.ContentEquals(receive.Pages[0]));
    }

    [Fact]
    public async Task ModemDisconnect_DuringPage_IsUncertain()
    {
        _line.Faults.OnData = c =>
        {
            if (c.Sender == "SIM-TX" && c.Data.Length > 3000) _txPort.Unplug();
            return c.Data;
        };
        var (send, _) = await RunSession([TestPages.Text()]);
        Assert.Equal(FaxOutcome.UnconfirmedDelivery, send.Outcome);
    }

    [Fact]
    public async Task ModemDisconnect_BeforePageData_IsRetryable()
    {
        _line.Faults.OnHdlc = c =>
        {
            if (c.Sender == "SIM-TX" && c.Fcf == Fcf.DCS) _txPort.Unplug();
            return FaultAction.Deliver;
        };
        var (send, _) = await RunSession([TestPages.Text()]);
        Assert.Equal(FaxOutcome.ModemDisconnected, send.Outcome);
        Assert.False(send.PageDataTransmitted);
        Assert.Equal(OutboundStatus.RetryWaiting, RetryPolicy.Decide(send.Outcome, 1, 3, new RedialSettings(), DateTime.UtcNow).Status);
    }

    [Fact]
    public async Task Cancellation_InterruptsTransmission()
    {
        CancellationTokenSource? sendCts = null;
        _line.Faults.OnHdlc = c =>
        {
            if (c.Sender == "SIM-RX" && c.Fcf == Fcf.CFR) sendCts?.Cancel();
            return FaultAction.Deliver;
        };
        var (send, receive) = await RunSession([TestPages.Text(), TestPages.Text("2", 2)], onStart: c => sendCts = c);
        Assert.Equal(FaxOutcome.Cancelled, send.Outcome);
        Assert.False(send.DeliveryConfirmed);
        Assert.NotEqual(FaxOutcome.Success, receive!.Outcome);
    }

    [Fact]
    public async Task UnsupportedModem_IsRejected()
    {
        var port = new SimulatedModemTransport(_line, "SIM-C2", new SimulatedModemOptions { FaxClasses = "0,2,2.0" });
        await port.OpenAsync(default);
        await using var modem = new Class1Modem(port, ModemProfiles.GenericClass1, _log);
        var ex = await Assert.ThrowsAsync<UnsupportedModemException>(() => modem.InitializeAsync(default));
        Assert.Contains("Class 1", ex.Message);
    }

    [Fact]
    public async Task ModemClaimingClass1WithoutModulations_IsRejected()
    {
        var port = new SimulatedModemTransport(_line, "SIM-FAKE", new SimulatedModemOptions { Modulations = "" });
        await port.OpenAsync(default);
        await using var modem = new Class1Modem(port, ModemProfiles.GenericClass1, _log);
        await Assert.ThrowsAsync<UnsupportedModemException>(() => modem.InitializeAsync(default));
    }

    [Fact]
    public void Initialization_SelectsClass10_AndReadsCapabilities()
    {
        Assert.Equal("1.0", _tx.Capabilities!.SelectedClass);
        Assert.Contains(145, _tx.Capabilities.TransmitModulations);
        Assert.Contains("Simulated", _tx.Identity!.Model);
        Assert.True(_tx.IsSimulated);
    }
}
