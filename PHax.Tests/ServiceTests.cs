using PHax.Core.Models;
using PHax.Core.Services;
using PHax.Data;
using PHax.Imaging.Encoding;
using PHax.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace PHax.Tests;

public sealed class ServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "phax-tests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private IDbContextFactory<FaxDbContext> _db = null!;
    private ServiceProvider _sp = null!;

    public async ValueTask InitializeAsync()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
        var services = new ServiceCollection().AddLogging();
        services.AddDbContextFactory<FaxDbContext>(o => FaxDbContext.Configure(o, _paths.DatabasePath));
        _sp = services.BuildServiceProvider();
        _db = _sp.GetRequiredService<IDbContextFactory<FaxDbContext>>();
        await FaxDbContext.InitializeAsync(_db);
    }

    public async ValueTask DisposeAsync()
    {
        await _sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task QueueRecovery_RequeuesSafeJobs_AndMarksInterruptedTransmissionsUncertain()
    {
        var dialing = new OutboundFax { Status = OutboundStatus.Dialing, FaxNumber = "1" };
        var transmitting = new OutboundFax { Status = OutboundStatus.Transmitting, FaxNumber = "2" };
        var sent = new OutboundFax { Status = OutboundStatus.Sent, FaxNumber = "3" };
        await using (var db = await _db.CreateDbContextAsync())
        {
            db.OutboundFaxes.AddRange(dialing, transmitting, sent);
            db.TransmissionAttempts.Add(new TransmissionAttempt { OutboundFaxId = dialing.Id });
            db.ReceivedFaxes.Add(new ReceivedFax { Status = ReceivedStatus.Receiving });
            await db.SaveChangesAsync();
        }

        var queue = new DbFaxJobQueue(_db);
        await queue.RecoverAsync(default);

        await using (var db = await _db.CreateDbContextAsync())
        {
            Assert.Equal(OutboundStatus.Queued, (await db.OutboundFaxes.FindAsync(dialing.Id))!.Status);
            Assert.Equal(OutboundStatus.Uncertain, (await db.OutboundFaxes.FindAsync(transmitting.Id))!.Status);
            Assert.Equal(OutboundStatus.Sent, (await db.OutboundFaxes.FindAsync(sent.Id))!.Status);
            Assert.Equal(ReceivedStatus.Failed, (await db.ReceivedFaxes.SingleAsync()).Status);
        }

        var next = await queue.TakeNextDueAsync(DateTime.UtcNow, default);
        Assert.Equal(dialing.Id, next!.Id);
        Assert.Null(await queue.TakeNextDueAsync(DateTime.UtcNow, default));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void LegacyFtiFaxData_IsMovedToPHax()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "phax-migrate", Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(baseDir, "FTIFax");
        var target = Path.Combine(baseDir, "PHax");
        try
        {
            Directory.CreateDirectory(Path.Combine(legacy, "Storage", "Inbox"));
            Directory.CreateDirectory(Path.Combine(target, "Logs")); // the installer pre-creates the new folder
            File.WriteAllText(Path.Combine(legacy, "ftifax.db"), "db");
            File.WriteAllText(Path.Combine(legacy, "ftifax.db-wal"), "wal");
            File.WriteAllText(Path.Combine(legacy, "Storage", "Inbox", "fax.tif"), "tif");

            var paths = new AppPaths(target);
            paths.MigrateLegacyData(legacy);

            Assert.Equal("db", File.ReadAllText(paths.DatabasePath));
            Assert.Equal("wal", File.ReadAllText(paths.DatabasePath + "-wal"));
            Assert.True(File.Exists(Path.Combine(target, "Storage", "Inbox", "fax.tif")));
            Assert.False(Directory.Exists(legacy));

            // Second start: nothing to do, nothing overwritten.
            File.WriteAllText(paths.DatabasePath, "newer");
            paths.MigrateLegacyData(legacy);
            Assert.Equal("newer", File.ReadAllText(paths.DatabasePath));
        }
        finally
        {
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Queue_HonoursRetryTimeAndPause()
    {
        var later = new OutboundFax { Status = OutboundStatus.RetryWaiting, NextAttemptUtc = DateTime.UtcNow.AddMinutes(5), FaxNumber = "1" };
        var paused = new OutboundFax { Status = OutboundStatus.Paused, FaxNumber = "2" };
        await using (var db = await _db.CreateDbContextAsync())
        {
            db.OutboundFaxes.AddRange(later, paused);
            await db.SaveChangesAsync();
        }
        var queue = new DbFaxJobQueue(_db);
        Assert.Null(await queue.TakeNextDueAsync(DateTime.UtcNow, default));
        Assert.Equal(later.Id, (await queue.TakeNextDueAsync(DateTime.UtcNow.AddMinutes(6), default))!.Id);
    }

    /// <summary>The whole service stack (supervisor, queue, processors, storage) against the built-in simulator.</summary>
    [Fact]
    [Trait("Category", "Simulation")]
    public async Task Service_WithSimulator_SendsQueuedJob_AndReceivesIncomingFax()
    {
        var store = new SettingsStore(_db);
        var settings = new AppSettings();
        settings.Modem.UseSimulator = true;
        settings.Incoming.AnswerAfterRings = 1;
        settings.Incoming.AutoConvertToPdf = true;
        await store.SaveAsync(settings);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddPHaxService(_paths, enablePipe: false);
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var status = host.Services.GetRequiredService<StatusHolder>();
            await WaitUntil(() => status.Current.ModemState == ModemState.Ready, TimeSpan.FromSeconds(30));
            Assert.True(status.Current.IsSimulated);

            // Queue a two-page job the same way the desktop app does.
            var job = new OutboundFax { Status = OutboundStatus.Queued, FaxNumber = "555-0142", RecipientName = "Test", QueuedUtc = DateTime.UtcNow, PageCount = 2 };
            var doc = Path.Combine(_paths.JobDirectory(job.Id), "fax.tif");
            TiffFax.Write(doc, [TestPages.Text("p1", 1), TestPages.Text("p2", 2)]);
            job.DocumentPath = _paths.MakeRelative(doc);
            await using (var db = await _db.CreateDbContextAsync())
            {
                db.OutboundFaxes.Add(job);
                await db.SaveChangesAsync();
            }
            host.Services.GetRequiredService<ModemSupervisor>().Enqueue(new NudgeRequest());

            OutboundFax? done = null;
            await WaitUntil(async () =>
            {
                await using var db = await _db.CreateDbContextAsync();
                done = await db.OutboundFaxes.Include(f => f.TransmissionAttempts).AsNoTracking().FirstAsync(f => f.Id == job.Id);
                return done.Status is OutboundStatus.Sent or OutboundStatus.Failed or OutboundStatus.Uncertain;
            }, TimeSpan.FromSeconds(60));
            Assert.Equal(OutboundStatus.Sent, done!.Status);
            Assert.True(done.IsSimulated);
            Assert.Equal(2, done.PagesConfirmed);
            Assert.Single(done.TransmissionAttempts);
            Assert.NotNull(done.SentUtc);

            // Incoming: the simulated remote calls us; the service answers after one ring and files the fax in the Inbox.
            host.Services.GetRequiredService<ModemSupervisor>().Enqueue(new SimulateIncomingRequest());
            ReceivedFax? received = null;
            await WaitUntil(async () =>
            {
                await using var db = await _db.CreateDbContextAsync();
                received = await db.ReceivedFaxes.AsNoTracking().FirstOrDefaultAsync(r => r.Status != ReceivedStatus.Receiving);
                return received is not null;
            }, TimeSpan.FromSeconds(60));
            Assert.Equal(ReceivedStatus.Complete, received!.Status);
            Assert.Equal(2, received.PageCount);
            Assert.True(received.IsSimulated);
            Assert.True(File.Exists(_paths.Resolve(received.TiffPath!)));
            Assert.True(File.Exists(_paths.Resolve(received.PdfPath!)));
            Assert.Equal(2, TiffFax.Read(_paths.Resolve(received.TiffPath!)).Count);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static Task WaitUntil(Func<bool> condition, TimeSpan timeout) => WaitUntil(() => Task.FromResult(condition()), timeout);

    private static async Task WaitUntil(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }
        Assert.Fail("Timed out waiting for condition.");
    }
}
