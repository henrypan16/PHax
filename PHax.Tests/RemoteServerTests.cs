using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using PHax.Core.Ipc;
using PHax.Core.Models;
using PHax.Core.Remote;
using PHax.Core.Services;
using PHax.Data;
using PHax.Imaging.Encoding;
using PHax.Service;
using PHax.Service.Remote;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace PHax.Tests;

/// <summary>
/// Runs the real service host (HTTPS listener and simulated modem) and talks to it with the real client over TLS on
/// the loopback interface. Serial hardware is not involved.
/// </summary>
public sealed class RemoteServerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "phax-tests", Guid.NewGuid().ToString("N"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private AppPaths _paths = null!;
    private IHost _host = null!;
    private int _port;

    private PairingService Pairing => _host.Services.GetRequiredService<PairingService>();
    private IDbContextFactory<FaxDbContext> Db => _host.Services.GetRequiredService<IDbContextFactory<FaxDbContext>>();
    private string Address => $"localhost:{_port}";

    public async ValueTask InitializeAsync()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        new NetworkConfigStore(_paths).Save(new NetworkConfig { Mode = NetworkMode.Server, AllowClients = true, Port = _port });

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddPHaxService(_paths, enablePipe: false);
        _host = builder.Build();
        await FaxDbContext.InitializeAsync(Db);
        await _host.Services.GetRequiredService<SettingsStore>().SaveAsync(new AppSettings { Modem = { UseSimulator = true } });
        await _host.StartAsync();
        await WaitUntilAsync(() => Task.FromResult(_host.Services.GetRequiredService<NetworkServerState>().Listening || _host.Services.GetRequiredService<NetworkServerState>().Error is not null));
        Assert.Null(_host.Services.GetRequiredService<NetworkServerState>().Error);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(50);
        }
    }

    private async Task<FaxServerClient> PairAsync(string name)
    {
        var (code, _) = Pairing.CreateCode();
        var result = await FaxServerClient.PairAsync(Address, code, name, Ct);
        return new FaxServerClient(ServerAddress.ToBaseUri(Address)!, result.Thumbprint, result.Token);
    }

    private static MemoryStream Tiff(int pages = 1) => new(TiffBytes(pages));

    private static byte[] TiffBytes(int pages)
    {
        using var ms = new MemoryStream();
        TiffFax.Write(ms, Enumerable.Range(1, pages).Select(i => TestPages.Text(seed: i)).ToList());
        return ms.ToArray();
    }

    private static SubmitFaxRequest Fax(string number = "5551234") => new("Dr Example", number, "Results", false, "");

    private async Task<int> JobCountAsync()
    {
        await using var db = await Db.CreateDbContextAsync(Ct);
        return await db.OutboundFaxes.CountAsync(Ct);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SubmittedFax_IsSentByTheServer_AndReportedToTheClient()
    {
        using var client = await PairAsync("Front Desk");
        var id = Guid.NewGuid();

        var queued = await client.SubmitAsync(id, Fax(), Tiff(2), Ct);

        Assert.Equal(OutboundStatus.Queued, queued.Status);
        await WaitUntilAsync(async () => (await client.GetOutboundAsync(id, Ct))?.Status == OutboundStatus.Sent, 60);
        var sent = (await client.ListOutboundAsync(OutboundView.Sent, null, Ct)).Single();
        Assert.Equal(id, sent.Id);
        Assert.Equal(2, sent.PageCount);
        Assert.True(sent.IsSimulated);
        Assert.Equal("Front Desk", sent.SubmittedBy);
        Assert.Equal(2, TiffFax.Read(await client.GetOutboundDocumentAsync(id, Ct)).Count);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RepeatedUpload_DoesNotCreateASecondJob_AndDifferentContentIsRefused()
    {
        using var client = await PairAsync("Front Desk");
        var id = Guid.NewGuid();

        await client.SubmitAsync(id, Fax(), Tiff(), Ct);
        var again = await client.SubmitAsync(id, Fax(), Tiff(), Ct);
        var changed = await Assert.ThrowsAsync<FaxServerException>(() => client.SubmitAsync(id, Fax(), Tiff(2), Ct));

        Assert.Equal(id, again.Id);
        Assert.True(changed.IsConflict);
        Assert.Equal(1, await JobCountAsync());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OutgoingFaxes_ArePrivateToTheSubmittingPc()
    {
        using var alice = await PairAsync("Alice PC");
        using var bob = await PairAsync("Bob PC");
        var id = Guid.NewGuid();
        await alice.SubmitAsync(id, Fax(), Tiff(), Ct);

        Assert.Null(await bob.GetOutboundAsync(id, Ct));
        Assert.Empty(await bob.ListOutboundAsync(OutboundView.Outbox, null, Ct));
        var open = await Assert.ThrowsAsync<FaxServerException>(() => bob.GetOutboundDocumentAsync(id, Ct));
        Assert.True(open.IsNotFound);
        var cancel = await Assert.ThrowsAsync<FaxServerException>(() => bob.OutboundActionAsync(id, "cancel", Ct));
        Assert.True(cancel.IsNotFound);
        var overwrite = await Assert.ThrowsAsync<FaxServerException>(() => bob.SubmitAsync(id, Fax(), Tiff(), Ct));
        Assert.True(overwrite.IsConflict);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Inbox_IsSharedBetweenPcs_WithoutExposingServerPaths()
    {
        using var alice = await PairAsync("Alice PC");
        using var bob = await PairAsync("Bob PC");
        var tiff = Path.Combine(_paths.InboxDirectory, "in.tif");
        TiffFax.Write(tiff, [TestPages.Text()]);
        var fax = new ReceivedFax { Status = ReceivedStatus.Complete, DisplayName = "Fax from clinic", PageCount = 1, TiffPath = _paths.MakeRelative(tiff), ProtocolLogPath = @"C:\secret\log.txt" };
        await using (var db = await Db.CreateDbContextAsync(Ct))
        {
            db.ReceivedFaxes.Add(fax);
            await db.SaveChangesAsync(Ct);
        }

        var listed = (await alice.ListReceivedAsync(false, null, Ct)).Single();
        Assert.Equal(RemoteDocument.Marker, listed.TiffPath);
        Assert.Null(listed.ProtocolLogPath);
        Assert.Single(TiffFax.Read(await bob.GetReceivedDocumentAsync(fax.Id, Ct)));
        Assert.Equal(1, await bob.GetUnreadCountAsync(Ct));

        await alice.SetReadAsync(fax.Id, true, Ct);
        await alice.RenameReceivedAsync(fax.Id, "Lab results", Ct);

        Assert.Equal(0, await bob.GetUnreadCountAsync(Ct));
        Assert.Equal("Lab results", (await bob.ListReceivedAsync(false, null, Ct)).Single().DisplayName);

        await bob.DeleteReceivedAsync(fax.Id, Ct);
        Assert.Empty(await alice.ListReceivedAsync(false, null, Ct));
        Assert.False(File.Exists(tiff));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EventStream_DeliversStatusAndOwnJobs_ButNotOtherPcsJobs()
    {
        using var alice = await PairAsync("Alice PC");
        using var bob = await PairAsync("Bob PC");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var bobSaw = new System.Collections.Concurrent.ConcurrentBag<PipeEnvelope>();
        var aliceSaw = new System.Collections.Concurrent.ConcurrentBag<PipeEnvelope>();
        int connected = 0;
        var streams = new[]
        {
            Task.Run(() => alice.ReadEventsAsync(aliceSaw.Add, () => Interlocked.Increment(ref connected), stop.Token), Ct),
            Task.Run(() => bob.ReadEventsAsync(bobSaw.Add, () => Interlocked.Increment(ref connected), stop.Token), Ct),
        };
        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref connected) == 2));

        var id = Guid.NewGuid();
        await alice.SubmitAsync(id, Fax(), Tiff(), Ct);
        await WaitUntilAsync(async () => (await alice.GetOutboundAsync(id, Ct))?.Status == OutboundStatus.Sent, 60);
        await WaitUntilAsync(() => Task.FromResult(aliceSaw.Any(e => e.Type == PipeEvents.JobChanged)));

        Assert.Contains(bobSaw, e => e.Type == PipeEvents.Status);
        Assert.DoesNotContain(bobSaw, e => e.Type is PipeEvents.JobChanged or PipeEvents.JobProgress);
        Assert.DoesNotContain(bobSaw, e => e.Type == PipeEvents.Notification);
        Assert.DoesNotContain(bobSaw, e => e.Type == PipeEvents.ProtocolEvent);
        Assert.DoesNotContain(aliceSaw, e => e.Type == PipeEvents.ProtocolEvent);

        stop.Cancel();
        await Task.WhenAll(streams.Select(s => s.ContinueWith(_ => { }, TaskScheduler.Default)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemovedPc_LosesAccessAtOnce_IncludingItsEventStream()
    {
        using var client = await PairAsync("Front Desk");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bool connected = false;
        var stream = Task.Run(() => client.ReadEventsAsync(_ => { }, () => connected = true, stop.Token), Ct);
        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref connected)));

        var id = (await Pairing.ListAsync(Ct)).Single().Id;
        await Pairing.RemoveAsync(id, Ct);

        var denied = await Assert.ThrowsAsync<FaxServerException>(() => client.ListReceivedAsync(false, null, Ct));
        Assert.True(denied.IsUnauthorized);
        await Assert.ThrowsAsync<FaxServerException>(() => stream.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Pairing_CodeWorksOnce_AndDiesAfterRepeatedWrongTries()
    {
        var (code, _) = Pairing.CreateCode();
        await FaxServerClient.PairAsync(Address, code, "First", Ct);
        await Assert.ThrowsAsync<FaxServerException>(() => FaxServerClient.PairAsync(Address, code, "Second", Ct));

        var (fresh, _) = Pairing.CreateCode();
        for (int i = 0; i < 5; i++)
            await Assert.ThrowsAsync<FaxServerException>(() => FaxServerClient.PairAsync(Address, "WRONG-CODE" + i, "Guesser", Ct));
        await Assert.ThrowsAsync<FaxServerException>(() => FaxServerClient.PairAsync(Address, fresh, "Late", Ct));
        Assert.Equal(["First"], (await Pairing.ListAsync(Ct)).Select(c => c.Name));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Pairing_ProofForAnotherCertificate_IsRefused()
    {
        // What a machine in the middle would forward: the client's proof is bound to the certificate the client saw.
        var (code, _) = Pairing.CreateCode();
        var proof = PairingProof.Client(code, new string('A', 64));
        var response = await Pairing.TryPairAsync("Attacker", proof, new string('B', 64), Ct);
        Assert.Null(response);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Requests_WithoutAValidToken_AreRejected()
    {
        using var stranger = new FaxServerClient(ServerAddress.ToBaseUri(Address)!, await ThumbprintAsync(), "not-a-token");
        var denied = await Assert.ThrowsAsync<FaxServerException>(() => stranger.ListReceivedAsync(false, null, Ct));
        Assert.True(denied.IsUnauthorized);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Client_RefusesAServerWithADifferentCertificate()
    {
        using var pinnedToSomethingElse = new FaxServerClient(ServerAddress.ToBaseUri(Address)!, new string('0', 64), "token");
        await Assert.ThrowsAsync<FaxServerException>(() => pinnedToSomethingElse.ListReceivedAsync(false, null, Ct));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Upload_ThatIsNotAFaxDocument_IsRefusedWithoutQueuing()
    {
        using var client = await PairAsync("Front Desk");
        var error = await Assert.ThrowsAsync<FaxServerException>(() => client.SubmitAsync(Guid.NewGuid(), Fax(), new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8, 9]), Ct));
        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
        await Assert.ThrowsAsync<FaxServerException>(() => client.SubmitAsync(Guid.NewGuid(), Fax("not a number!"), Tiff(), Ct));
        Assert.Equal(0, await JobCountAsync());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ClientMode_LeavesTheModemAlone()
    {
        new NetworkConfigStore(_paths).Save(new NetworkConfig { Mode = NetworkMode.Client });
        var supervisor = _host.Services.GetRequiredService<ModemSupervisor>();
        supervisor.Enqueue(new ReloadRequest());
        var state = _host.Services.GetRequiredService<NetworkServerState>();
        state.RequestReload();
        var status = _host.Services.GetRequiredService<StatusHolder>();

        await WaitUntilAsync(() => Task.FromResult(status.Current.ModemState == ModemState.NotConfigured && status.Current.PortName is null && !state.Listening));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Configuration_FromTheInstaller_SurvivesAndKeepsPairing()
    {
        var store = new NetworkConfigStore(new AppPaths(Path.Combine(_root, "installer")));
        NetworkCli.Configure(store, ["--configure-network", "--mode", "server", "--allow-clients", "--port", "50000"]);
        Assert.Equal((NetworkMode.Server, true, 50000), (store.Load().Mode, store.Load().AllowClients, store.Load().Port));

        var (code, _) = Pairing.CreateCode();
        await store.PairAsync(Address, code, Ct);
        var paired = store.Load();
        Assert.Equal(NetworkMode.Client, paired.Mode);
        Assert.True(paired.IsPaired);
        Assert.False(string.IsNullOrEmpty(NetworkConfigStore.UnprotectToken(paired.ProtectedToken)));
        Assert.DoesNotContain(NetworkConfigStore.UnprotectToken(paired.ProtectedToken)!, File.ReadAllText(Path.Combine(_root, "installer", "network.json")));

        Assert.Throws<ArgumentException>(() => NetworkCli.Configure(store, ["--configure-network", "--mode", "elsewhere"]));
        Assert.Throws<ArgumentException>(() => NetworkCli.Configure(store, ["--configure-network", "--mode", "server", "--port", "80"]));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListenerThatCannotStart_IsReported_AndLeavesTheModemRunning()
    {
        using var occupied = new TcpListener(IPAddress.IPv6Any, 0) { Server = { DualMode = true } };
        occupied.Start();
        int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var state = _host.Services.GetRequiredService<NetworkServerState>();
        var status = _host.Services.GetRequiredService<StatusHolder>();
        await WaitUntilAsync(() => Task.FromResult(status.Current.ModemState == ModemState.Ready), 30);

        new NetworkConfigStore(_paths).Save(new NetworkConfig { Mode = NetworkMode.Server, AllowClients = true, Port = port });
        state.RequestReload();
        await WaitUntilAsync(() => Task.FromResult(state.Error is not null));

        Assert.False(state.Listening);
        Assert.Contains(port.ToString(), state.Error);
        await Task.Delay(500, Ct);
        Assert.Equal(ModemState.Ready, status.Current.ModemState);

        occupied.Stop();
        state.RequestReload();
        await WaitUntilAsync(() => Task.FromResult(state.Listening));
        Assert.Null(state.Error);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("pharmacy-pc", "pharmacy-pc", NetworkConfig.DefaultPort)]
    [InlineData("pharmacy-pc:9000", "pharmacy-pc", 9000)]
    [InlineData(" 192.168.1.5 ", "192.168.1.5", NetworkConfig.DefaultPort)]
    [InlineData("[fe80::1]:9000", "[fe80::1]", 9000)]
    public void ServerAddress_Parses(string text, string host, int port)
    {
        var uri = ServerAddress.ToBaseUri(text)!;
        Assert.Equal("https", uri.Scheme);
        Assert.Equal(host, uri.Host);
        Assert.Equal(port, uri.Port);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("")]
    [InlineData("http://evil/")]
    [InlineData("host/path")]
    [InlineData("user@host")]
    public void ServerAddress_RejectsAnythingButAnAddress(string text) => Assert.Null(ServerAddress.ToBaseUri(text));

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("192.168.0.9", true)]
    [InlineData("::ffff:192.168.0.9", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void LocalNetwork_RecognisesPrivateAddresses(string address, bool local) =>
        Assert.Equal(local, LocalNetwork.IsLocal(IPAddress.Parse(address)));

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateUpload_AcceptsPHaxDocuments_AndRejectsDamagedOnes()
    {
        var good = TiffBytes(2);
        Assert.Equal(2, TiffFax.ValidateUpload(good, 10));
        Assert.Throws<InvalidDataException>(() => TiffFax.ValidateUpload(good, 1));
        Assert.Throws<InvalidDataException>(() => TiffFax.ValidateUpload([.. good.Take(40)], 10));

        // Overwrite every byte in turn: the check may accept or refuse, but only ever with InvalidDataException,
        // never by crashing the service or allocating what a forged header asks for.
        var random = new Random(1234);
        for (int i = 0; i < 3000; i++)
        {
            var bad = (byte[])good.Clone();
            bad[random.Next(bad.Length)] = (byte)random.Next(256);
            try { TiffFax.ValidateUpload(bad, 10); } catch (InvalidDataException) { }
        }
    }

    /// <summary>The thumbprint as a first connection sees it, through a throwaway pairing.</summary>
    private async Task<string> ThumbprintAsync()
    {
        var (code, _) = Pairing.CreateCode();
        return (await FaxServerClient.PairAsync(Address, code, "Probe", Ct)).Thumbprint;
    }
}
