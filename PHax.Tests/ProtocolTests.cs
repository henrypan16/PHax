using PHax.Core.Models;
using PHax.Core.Services;
using PHax.Modem.Class1;
using PHax.Modem.T30;
using Xunit;

namespace PHax.Tests;

[Trait("Category", "Unit")]
public class ProtocolTests
{
    [Fact]
    public void Fcs_MatchesIso3309CheckValue()
    {
        // CRC-16/X-25 (ISO 3309 HDLC FCS) check value for "123456789".
        Assert.Equal(0x906E, HdlcFcs.Compute("123456789"u8));
        var frame = T30Frame.Build(Fcf.DIS, [0x00, 0xEE, 0x78], final: true);
        var withFcs = HdlcFcs.Append(frame);
        Assert.True(HdlcFcs.Check(withFcs));
        withFcs[3] ^= 1;
        Assert.False(HdlcFcs.Check(withFcs));
    }

    [Fact]
    public void Dle_EscapeAndDecode_RoundTrip()
    {
        byte[] data = [0x00, 0x10, 0x10, 0x03, 0xFF, 0x10];
        var escaped = Dle.Escape(data);
        Assert.Equal(new byte[] { 0x00, 0x10, 0x10, 0x10, 0x10, 0x03, 0xFF, 0x10, 0x10 }, escaped);
        var dec = new DleDecoder();
        foreach (var b in escaped.Concat(Dle.Terminator)) dec.Feed(b);
        Assert.True(dec.Complete);
        Assert.Equal(data, dec.ToArray());

        var sub = new DleDecoder();
        foreach (var b in new byte[] { 0x41, 0x10, 0x1A, 0x42, 0x10, 0x03 }) sub.Feed(b);
        Assert.Equal(new byte[] { 0x41, 0x10, 0x10, 0x42 }, sub.ToArray());
    }

    [Fact]
    public void Frame_WithLeadingHdlcFlag_Parses()
    {
        // Exact bytes from a CX93001: the opening flag 0x7E was passed through before the DIS frame.
        var frame = T30Frame.TryParse(Convert.FromHexString("7EFF1380006E78"));
        Assert.NotNull(frame);
        Assert.Equal(Fcf.DIS, frame.Fcf);
        Assert.True(frame.Final);
        Assert.Equal(new byte[] { 0x00, 0x6E, 0x78 }, frame.Fif);
    }

    [Fact]
    public void Fcf_XBitNormalisation()
    {
        Assert.Equal(Fcf.DCS, Fcf.Normalize(Fcf.DCS | Fcf.X));
        Assert.Equal(Fcf.MCF, Fcf.Normalize(Fcf.MCF));
        // DIS and DTC differ only in bit 0 and must not be merged.
        Assert.Equal(Fcf.DIS, Fcf.Normalize(Fcf.DIS));
        Assert.Equal(Fcf.DTC, Fcf.Normalize(Fcf.DTC));
        Assert.Equal("EOP", Fcf.Name(Fcf.EOP | Fcf.X));
    }

    [Fact]
    public void Dis_V17FineMr_Parses()
    {
        // Constructed per T.30 Table 2: bits 10, 11, 12, 14 (V.27ter+V.29+V.17), 15 (fine), 16 (MR),
        // 20 (unlimited length), 21-23 = 111 (0 ms).
        var fif = new byte[] { 0x00, 0b1110_1110, 0b0111_1000 };
        var dis = Capabilities.ParseDis(fif);
        Assert.True(dis.CanReceive);
        Assert.True(dis.Fine);
        Assert.True(dis.TwoDimensional);
        Assert.Contains(FaxModulation.V17_14400, dis.Modulations);
        Assert.Contains(FaxModulation.V29_9600, dis.Modulations);
        Assert.Equal(0, dis.MinScanLineMs);
        Assert.Equal(2, dis.LengthCode);
    }

    [Fact]
    public void Dis_V29Only_Parses()
    {
        var fif = new byte[3];
        Capabilities.SetBit(fif, 10, true);
        Capabilities.SetBit(fif, 11, true); // bits 11-14 = 1,0,0,0 -> V.29
        var dis = Capabilities.ParseDis(fif);
        Assert.DoesNotContain(dis.Modulations, m => m.Family == ModulationFamily.V17);
        Assert.Contains(FaxModulation.V29_9600, dis.Modulations);
        Assert.Contains(FaxModulation.V27_2400, dis.Modulations);
        Assert.Equal(20, dis.MinScanLineMs);
    }

    [Theory]
    [MemberData(nameof(AllModulations))]
    public void Dcs_RoundTripsEveryModulation(string name)
    {
        var mod = FaxModulation.All.Single(m => m.Name == name);
        foreach (var ms in new[] { 0, 5, 10, 20, 40 })
        {
            var dcs = Capabilities.ParseDcs(Capabilities.BuildDcs(mod, fine: true, mr: false, ms));
            Assert.NotNull(dcs);
            Assert.Equal(mod, dcs.Modulation);
            Assert.True(dcs.Fine);
            Assert.False(dcs.TwoDimensional);
            Assert.Equal(ms, dcs.MinScanLineMs);
            Assert.Equal(1728, dcs.Width);
        }
    }

    public static TheoryData<string> AllModulations => new(FaxModulation.All.Select(m => m.Name));

    [Fact]
    public void Dis_BuiltForV17Modem_AdvertisesAllFamilies()
    {
        var dis = Capabilities.ParseDis(Capabilities.BuildDis(FaxModulation.All, fine: true, mr: true));
        Assert.Equal(FaxModulation.All.Count, dis.Modulations.Count);
        Assert.True(dis.CanReceive && dis.Fine && dis.TwoDimensional);
    }

    [Fact]
    public void StationIdentifier_IsReversedAndPadded()
    {
        var fif = StationIdentifier.Encode("+1 555 0100");
        Assert.Equal(20, fif.Length);
        Assert.Equal((byte)' ', fif[0]);
        Assert.Equal((byte)'0', fif[9]);
        Assert.Equal((byte)'+', fif[19]);
        Assert.Equal("+1 555 0100", StationIdentifier.Decode(fif));
    }

    [Fact]
    public void TcfEvaluation()
    {
        var good = new byte[(int)(FaxModulation.V29_9600.BytesPerSecond * 1.5)];
        Assert.True(T30Receiver.IsTcfGood(good, FaxModulation.V29_9600, 0.75));
        var bad = new byte[good.Length];
        new Random(1).NextBytes(bad);
        Assert.False(T30Receiver.IsTcfGood(bad, FaxModulation.V29_9600, 0.75));
    }

    [Fact]
    public void Class1ListParsing()
    {
        Assert.Equal(["0", "1", "1.0", "2", "8"], Class1Modem.ParseList("0,1,1.0,2,8"));
        Assert.Equal(["0", "1", "2"], Class1Modem.ParseList("(0-2)"));
        Assert.Equal([24, 48, 72, 96, 145, 146], Class1Modem.ParseInts("24,48,72,96,145,146"));
    }

    [Theory]
    [InlineData(FaxOutcome.Busy, OutboundStatus.RetryWaiting)]
    [InlineData(FaxOutcome.NoAnswer, OutboundStatus.RetryWaiting)]
    [InlineData(FaxOutcome.TrainingFailed, OutboundStatus.RetryWaiting)]
    [InlineData(FaxOutcome.UnconfirmedDelivery, OutboundStatus.Uncertain)]
    [InlineData(FaxOutcome.Cancelled, OutboundStatus.Cancelled)]
    [InlineData(FaxOutcome.PermanentFailure, OutboundStatus.Failed)]
    [InlineData(FaxOutcome.Success, OutboundStatus.Sent)]
    public void RetryPolicy_Decisions(FaxOutcome outcome, OutboundStatus expected)
    {
        var d = RetryPolicy.Decide(outcome, attemptsMade: 1, maxAttempts: 3, new RedialSettings(), DateTime.UtcNow);
        Assert.Equal(expected, d.Status);
        Assert.Equal(expected == OutboundStatus.RetryWaiting, d.NextAttemptUtc.HasValue);
    }

    [Fact]
    public void RetryPolicy_RespectsLimitsAndSwitches()
    {
        var settings = new RedialSettings { MaxAttempts = 2, RetryOnBusy = false };
        Assert.Equal(OutboundStatus.Failed, RetryPolicy.Decide(FaxOutcome.Busy, 1, 5, settings, DateTime.UtcNow).Status);
        Assert.Equal(OutboundStatus.Failed, RetryPolicy.Decide(FaxOutcome.NoAnswer, 2, 5, settings, DateTime.UtcNow).Status);
        Assert.Equal(OutboundStatus.Failed,
            RetryPolicy.Decide(FaxOutcome.Busy, 1, 5, new RedialSettings { Enabled = false }, DateTime.UtcNow).Status);
    }

    [Fact]
    public void DialString_Normalises()
    {
        Assert.Equal("915551234567", DialString.Build("9", "+1 (555) 123-4567"));
        Assert.Equal("9,5551234", DialString.Build("9,", "555-1234"));
        Assert.False(DialString.IsValid("12"));
    }
}
