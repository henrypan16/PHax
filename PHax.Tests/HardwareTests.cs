using PHax.Core.Models;
using PHax.Core.Services;
using PHax.Imaging.Decoding;
using PHax.Imaging.Encoding;
using PHax.Modem.Class1;
using PHax.Modem.Profiles;
using PHax.Modem.Serial;
using PHax.Modem.T30;
using Xunit;

namespace PHax.Tests;

/// <summary>
/// REAL HARDWARE tests. Skipped unless configured:
///   PHAX_HW_PORT      COM port of the modem under test (e.g. COM3)
///   PHAX_HW_PROFILE   startech-cx93010 | usr5637 | generic-class1
///   PHAX_HW_DIAL      number of a real fax machine for the send test (optional)
///   PHAX_HW_RECEIVE   set to 1 to wait up to 5 minutes for a real incoming fax (optional)
/// The service must be stopped first: only one process can own the COM port.
/// Protocol logs are written to %TEMP%\phax-hw-*.log.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class HardwareTests
{
    private static string? Port => Environment.GetEnvironmentVariable("PHAX_HW_PORT");
    private static ModemProfile Profile => ModemProfiles.Get(Environment.GetEnvironmentVariable("PHAX_HW_PROFILE"));

    private static async Task<(Class1Modem Modem, ProtocolLog Log)> OpenAsync(string name)
    {
        var log = new ProtocolLog(20000);
        log.BeginSessionFile(Path.GetTempPath(), $"phax-hw-{name}");
        var transport = new SerialPortTransport(Port!, 115200, hardwareFlowControl: true);
        await transport.OpenAsync(default);
        return (new Class1Modem(transport, Profile, log), log);
    }

    [Fact]
    public async Task Hardware_Initialization_ReportsClass1()
    {
        Assert.SkipUnless(!string.IsNullOrEmpty(Port), "PHAX_HW_PORT not set; no modem under test.");
        var (modem, log) = await OpenAsync("init");
        await using (modem)
        {
            var caps = await modem.InitializeAsync(default);
            Assert.True(caps.SupportsClass1);
            Assert.NotEmpty(caps.TransmitModulations);
            Assert.NotEmpty(caps.ReceiveModulations);
        }
        log.EndSessionFile();
    }

    [Fact]
    public async Task Hardware_SendFax_IsConfirmed()
    {
        var dial = Environment.GetEnvironmentVariable("PHAX_HW_DIAL");
        Assert.SkipUnless(!string.IsNullOrEmpty(Port) && !string.IsNullOrEmpty(dial), "PHAX_HW_PORT / PHAX_HW_DIAL not set.");
        var (modem, log) = await OpenAsync("send");
        await using (modem)
        {
            await modem.InitializeAsync(default);
            var engine = new Class1FaxEngine(new T4Encoder(), new T4Decoder(), log, T30Options.FromProfile(Profile));
            var result = await engine.CreateSender(modem).SendAsync(new FaxSendRequest
            {
                DialString = DialString.Build("", dial!),
                Pages = [TestPages.Text("PHax hardware test page 1", 1), TestPages.Patterns()],
                LocalId = "PHAX TEST",
            }, default);
            Assert.True(result.Outcome == FaxOutcome.Success, $"{result.Outcome}: {result.Message}");
            Assert.True(result.DeliveryConfirmed);
        }
        log.EndSessionFile();
    }

    [Fact]
    public async Task Hardware_ReceiveFax_IsComplete()
    {
        Assert.SkipUnless(!string.IsNullOrEmpty(Port) && Environment.GetEnvironmentVariable("PHAX_HW_RECEIVE") == "1",
            "PHAX_HW_PORT / PHAX_HW_RECEIVE not set.");
        var (modem, log) = await OpenAsync("receive");
        await using (modem)
        {
            await modem.InitializeAsync(default);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            ModemUnsolicitedEvent? e;
            do e = await modem.WaitForUnsolicitedAsync(TimeSpan.FromMinutes(5), cts.Token);
            while (e is not null && e.Kind != "RING");
            Assert.NotNull(e);
            var engine = new Class1FaxEngine(new T4Encoder(), new T4Decoder(), log, T30Options.FromProfile(Profile));
            var result = await engine.CreateReceiver(modem).ReceiveAsync(new FaxReceiveRequest { LocalId = "PHAX TEST" }, cts.Token);
            Assert.True(result.Complete, $"{result.Outcome}: {result.Message}");
            TiffFax.Write(Path.Combine(Path.GetTempPath(), $"phax-hw-received-{DateTime.Now:yyyyMMdd-HHmmss}.tif"), result.Pages);
        }
        log.EndSessionFile();
    }

    [Fact]
    public void Hardware_PortDiscovery_ListsConfiguredPort()
    {
        Assert.SkipUnless(!string.IsNullOrEmpty(Port), "PHAX_HW_PORT not set.");
        Assert.Contains(SerialPortDiscovery.List(), p => p.PortName.Equals(Port, StringComparison.OrdinalIgnoreCase));
    }
}
