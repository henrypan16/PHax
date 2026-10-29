using PHax.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PHax.Data;

public sealed class FaxDbContext(DbContextOptions<FaxDbContext> options) : DbContext(options)
{
    public DbSet<OutboundFax> OutboundFaxes => Set<OutboundFax>();
    public DbSet<FaxAttachment> Attachments => Set<FaxAttachment>();
    public DbSet<TransmissionAttempt> TransmissionAttempts => Set<TransmissionAttempt>();
    public DbSet<ReceivedFax> ReceivedFaxes => Set<ReceivedFax>();
    public DbSet<Recipient> Recipients => Set<Recipient>();
    public DbSet<ModemConfiguration> ModemConfigurations => Set<ModemConfiguration>();
    public DbSet<AppSettingRecord> Settings => Set<AppSettingRecord>();
    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();
    public DbSet<BackupRecord> Backups => Set<BackupRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<OutboundFax>(e =>
        {
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.NextAttemptUtc);
            e.HasMany(x => x.Attachments).WithOne().HasForeignKey(a => a.OutboundFaxId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.TransmissionAttempts).WithOne().HasForeignKey(a => a.OutboundFaxId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.RecipientName).HasMaxLength(200);
            e.Property(x => x.FaxNumber).HasMaxLength(64);
            e.Property(x => x.Subject).HasMaxLength(500);
        });
        b.Entity<ReceivedFax>(e =>
        {
            e.HasIndex(x => x.ReceivedUtc);
            e.HasIndex(x => x.Status);
        });
        b.Entity<Recipient>().HasIndex(x => x.FaxNumber);
        b.Entity<AppSettingRecord>().HasKey(x => x.Key);
        b.Entity<NotificationRecord>().HasIndex(x => x.CreatedUtc);
        b.Entity<BackupRecord>().HasIndex(x => x.CreatedUtc);
    }

    public static string ConnectionString(string databasePath) => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        // Retries on SQLITE_BUSY while the other process (app or service) holds the write lock.
        DefaultTimeout = 30,
        Pooling = true,
    }.ToString();

    public static void Configure(DbContextOptionsBuilder options, string databasePath) =>
        options.UseSqlite(ConnectionString(databasePath));

    /// <summary>Applies migrations and enables WAL so the service and the desktop app can share the database.</summary>
    public static async Task InitializeAsync(IDbContextFactory<FaxDbContext> factory, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}

/// <summary>Used by <c>dotnet ef</c> at design time.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<FaxDbContext>
{
    public FaxDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FaxDbContext>();
        Configure(options, Path.Combine(Path.GetTempPath(), "phax-design.db"));
        return new FaxDbContext(options.Options);
    }

    private static void Configure(DbContextOptionsBuilder options, string path) => FaxDbContext.Configure(options, path);
}
