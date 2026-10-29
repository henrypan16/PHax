using System.IO.Compression;
using PHax.Core.Models;
using PHax.Core.Services;
using PHax.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace PHax.Service;

/// <summary>Zip backups of documents, database and logs; scheduled and manual; restore with the modem released.</summary>
public sealed class BackupService(
    IDbContextFactory<FaxDbContext> dbFactory,
    SettingsStore settingsStore,
    AppPaths paths,
    IServiceProvider services,
    ILogger<BackupService> log) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(15), stopping);
                var settings = await settingsStore.LoadAsync(stopping);
                if (!settings.Backup.Enabled) continue;
                await using var db = await dbFactory.CreateDbContextAsync(stopping);
                var last = await db.Backups.Where(b => b.Success).OrderByDescending(b => b.CreatedUtc).Select(b => (DateTime?)b.CreatedUtc).FirstOrDefaultAsync(stopping);
                if (last is null || DateTime.UtcNow - last > TimeSpan.FromHours(Math.Max(1, settings.Backup.ScheduleIntervalHours)))
                    await RunAsync(scheduled: true, stopping);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Scheduled backup failed");
            }
        }
    }

    public async Task<BackupRecord> RunAsync(bool scheduled, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var settings = await settingsStore.LoadAsync(ct);
        var record = new BackupRecord { Scheduled = scheduled };
        try
        {
            var dir = string.IsNullOrWhiteSpace(settings.Backup.BackupDirectory) ? paths.DefaultBackupDirectory : settings.Backup.BackupDirectory;
            Directory.CreateDirectory(dir);
            var zipPath = Path.Combine(dir, $"PHax-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            record.Path = zipPath;
            var tmp = zipPath + ".tmp";
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                if (settings.Backup.IncludeDatabase)
                {
                    // VACUUM INTO produces a consistent snapshot while the database stays in use.
                    var snapshot = Path.Combine(Path.GetTempPath(), $"phax-{Guid.NewGuid():N}.db");
                    await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", [snapshot], ct);
                    zip.CreateEntryFromFile(snapshot, "database/phax.db");
                    SqliteConnection.ClearAllPools();
                    File.Delete(snapshot);
                }
                if (settings.Backup.IncludeInbox) AddDirectory(zip, paths.InboxDirectory, "storage/Inbox");
                var jobStatuses = new List<OutboundStatus>();
                if (settings.Backup.IncludeSent) jobStatuses.Add(OutboundStatus.Sent);
                if (settings.Backup.IncludeDrafts) jobStatuses.Add(OutboundStatus.Draft);
                if (jobStatuses.Count > 0)
                {
                    var ids = await db.OutboundFaxes.Where(f => jobStatuses.Contains(f.Status)).Select(f => f.Id).ToListAsync(ct);
                    foreach (var id in ids)
                        AddDirectory(zip, paths.JobDirectory(id), $"storage/Jobs/{id:N}");
                }
                if (settings.Backup.IncludeLogs) AddDirectory(zip, paths.LogsDirectory, "logs", skipLocked: true);
            }
            File.Move(tmp, zipPath, overwrite: true);
            record.SizeBytes = new FileInfo(zipPath).Length;
            record.Success = true;
            record.Message = "Backup completed.";
            Prune(dir, settings.Backup.KeepBackups);
            log.LogInformation("Backup written to {Path}", zipPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            record.Success = false;
            record.Message = ex.Message;
            log.LogError(ex, "Backup failed");
        }
        finally
        {
            _gate.Release();
        }
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            db.Backups.Add(record);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return record;
    }

    /// <summary>Restores a backup. The modem is released for the duration and the database is replaced atomically.</summary>
    public async Task RestoreAsync(string zipPath, CancellationToken ct)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("Backup not found.", zipPath);
        var supervisor = services.GetRequiredService<ModemSupervisor>();
        var suspended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.Enqueue(new SuspendRequest(suspended, resume.Task));
        await suspended.Task.WaitAsync(TimeSpan.FromMinutes(20), ct);
        await _gate.WaitAsync(ct);
        try
        {
            var staging = Path.Combine(Path.GetTempPath(), $"phax-restore-{Guid.NewGuid():N}");
            ZipFile.ExtractToDirectory(zipPath, staging);
            var db = Path.Combine(staging, "database", "phax.db");
            if (File.Exists(db))
            {
                SqliteConnection.ClearAllPools();
                File.Copy(paths.DatabasePath, paths.DatabasePath + ".before-restore", overwrite: true);
                foreach (var suffix in new[] { "-wal", "-shm" })
                    if (File.Exists(paths.DatabasePath + suffix)) File.Delete(paths.DatabasePath + suffix);
                File.Copy(db, paths.DatabasePath, overwrite: true);
            }
            CopyDirectory(Path.Combine(staging, "storage"), paths.StorageRoot);
            Directory.Delete(staging, recursive: true);
            await FaxDbContext.InitializeAsync(dbFactory, ct);
            log.LogInformation("Restored backup {Path}", zipPath);
        }
        finally
        {
            _gate.Release();
            resume.TrySetResult();
        }
    }

    private static void AddDirectory(ZipArchive zip, string dir, string prefix, bool skipLocked = false)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var entry = $"{prefix}/{Path.GetRelativePath(dir, file).Replace('\\', '/')}";
            try
            {
                using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = zip.CreateEntry(entry, CompressionLevel.Optimal).Open();
                src.CopyTo(dst);
            }
            catch (IOException) when (skipLocked) { }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void Prune(string dir, int keep)
    {
        var old = new DirectoryInfo(dir).GetFiles("PHax-backup-*.zip").OrderByDescending(f => f.CreationTimeUtc).Skip(Math.Max(1, keep));
        foreach (var f in old)
        {
            try { f.Delete(); } catch (IOException) { }
        }
    }
}
