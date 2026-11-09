using PHax.Core.Models;
using PHax.Service;
using Xunit;

namespace PHax.Tests;

[Trait("Category", "Unit")]
public class ReceiveFallbackPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 15, 0, 0, DateTimeKind.Utc);

    private static ReceivedFax Rx(TimeSpan ago, ReceivedStatus status, string? modulation, string? caller = null) =>
        new() { ReceivedUtc = Now - ago, Status = status, Modulation = modulation, CallerId = caller };

    [Fact]
    public void NoHistory_OffersV17() =>
        Assert.Null(ReceiveFallbackPolicy.V17BlockReason("5551234", [], Now));

    [Fact]
    public void KnownCaller_FailedAtV17_IsOfferedV29Only()
    {
        var reason = ReceiveFallbackPolicy.V17BlockReason("5551234",
            [Rx(TimeSpan.FromMinutes(2), ReceivedStatus.Failed, "V.17 14400", "5551234")], Now);
        Assert.NotNull(reason);
        Assert.Contains("V.17 14400", reason);
    }

    [Fact]
    public void KnownCaller_StaysOnV29AfterTheFallbackWorked()
    {
        // Failed at V.17, then succeeded at V.29: the next fax from the same number must not go back to V.17.
        Assert.NotNull(ReceiveFallbackPolicy.V17BlockReason("5551234",
        [
            Rx(TimeSpan.FromDays(3), ReceivedStatus.Failed, "V.17 14400", "5551234"),
            Rx(TimeSpan.FromDays(3) - TimeSpan.FromMinutes(2), ReceivedStatus.Complete, "V.29 9600", "5551234"),
        ], Now));
    }

    [Fact]
    public void KnownCaller_OldFailureExpires() =>
        Assert.Null(ReceiveFallbackPolicy.V17BlockReason("5551234",
            [Rx(TimeSpan.FromDays(31), ReceivedStatus.Failed, "V.17 14400", "5551234")], Now));

    [Fact]
    public void OtherCallersFailure_DoesNotAffectKnownCaller() =>
        Assert.Null(ReceiveFallbackPolicy.V17BlockReason("5559999",
        [
            Rx(TimeSpan.FromMinutes(1), ReceivedStatus.Failed, "V.17 14400", "5551234"),
            Rx(TimeSpan.FromMinutes(1), ReceivedStatus.Failed, "V.17 14400"),
        ], Now));

    [Fact]
    public void PartialAtV17_Counts() =>
        Assert.NotNull(ReceiveFallbackPolicy.V17BlockReason("5551234",
            [Rx(TimeSpan.FromMinutes(5), ReceivedStatus.Partial, "V.17 12000", "5551234")], Now));

    [Theory]
    [InlineData("V.29 9600")]
    [InlineData(null)] // e.g. a voice call that never got to DCS
    public void FailureNotAtV17_DoesNotBlock(string? modulation) =>
        Assert.Null(ReceiveFallbackPolicy.V17BlockReason("5551234",
            [Rx(TimeSpan.FromMinutes(1), ReceivedStatus.Failed, modulation, "5551234")], Now));

    [Fact]
    public void UnknownCaller_RecentV17Failure_IsOfferedV29Only() =>
        Assert.NotNull(ReceiveFallbackPolicy.V17BlockReason(null,
            [Rx(TimeSpan.FromMinutes(3), ReceivedStatus.Failed, "V.17 14400")], Now));

    [Fact]
    public void UnknownCaller_V29AttemptAlsoFailed_StillNoV17()
    {
        Assert.NotNull(ReceiveFallbackPolicy.V17BlockReason(" ",
        [
            Rx(TimeSpan.FromMinutes(6), ReceivedStatus.Failed, "V.17 14400"),
            Rx(TimeSpan.FromMinutes(3), ReceivedStatus.Failed, "V.29 9600"),
        ], Now));
    }

    [Fact]
    public void UnknownCaller_OnlyShortWindow() =>
        Assert.Null(ReceiveFallbackPolicy.V17BlockReason(null,
            [Rx(TimeSpan.FromMinutes(20), ReceivedStatus.Failed, "V.17 14400")], Now));

    [Fact]
    public void UnknownCaller_LaterCompleteReception_ClearsIt() =>
        Assert.Null(ReceiveFallbackPolicy.V17BlockReason(null,
        [
            Rx(TimeSpan.FromMinutes(8), ReceivedStatus.Failed, "V.17 14400"),
            Rx(TimeSpan.FromMinutes(4), ReceivedStatus.Complete, "V.29 9600"),
        ], Now));

    [Fact]
    public void UnknownCaller_IgnoresFailuresWithCallerId() =>
        Assert.Null(ReceiveFallbackPolicy.V17BlockReason(null,
            [Rx(TimeSpan.FromMinutes(1), ReceivedStatus.Failed, "V.17 14400", "5551234")], Now));
}
