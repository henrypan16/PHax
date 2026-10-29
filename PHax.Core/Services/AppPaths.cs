namespace PHax.Core.Services;

/// <summary>
/// File-system layout shared by the service and the desktop app.
/// The database and logs always live under ProgramData so both processes can find them;
/// documents live under the configurable storage root.
/// </summary>
public sealed class AppPaths
{
    public static string DefaultDataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PHax");

    public string DataRoot { get; }
    public string StorageRoot { get; private set; }

    public AppPaths(string? dataRoot = null, string? storageRoot = null)
    {
        DataRoot = string.IsNullOrWhiteSpace(dataRoot)
            ? Environment.GetEnvironmentVariable("PHAX_DATA_ROOT") is { Length: > 0 } env ? env : DefaultDataRoot
            : dataRoot;
        StorageRoot = string.IsNullOrWhiteSpace(storageRoot) ? Path.Combine(DataRoot, "Storage") : storageRoot;
    }

    public string DatabasePath => Path.Combine(DataRoot, "phax.db");
    public string LogsDirectory => Path.Combine(DataRoot, "Logs");
    public string ProtocolLogsDirectory => Path.Combine(DataRoot, "Logs", "Protocol");
    public string JobsDirectory => Path.Combine(StorageRoot, "Jobs");
    public string InboxDirectory => Path.Combine(StorageRoot, "Inbox");
    public string FailedReceptionsDirectory => Path.Combine(StorageRoot, "FailedReceptions");
    public string DefaultBackupDirectory => Path.Combine(DataRoot, "Backups");

    /// <summary>
    /// Moves data left by installations made before the project was renamed (FTIFax) into the PHax folders:
    /// ProgramData\FTIFax → ProgramData\PHax (ftifax.db → phax.db). Runs only for the default data root and only
    /// while no PHax database exists, so it is a no-op after the first start. Called by the service (Local System).
    /// </summary>
    public void MigrateLegacyData(string? legacyRoot = null)
    {
        var legacy = legacyRoot ?? Path.Combine(Path.GetDirectoryName(DefaultDataRoot)!, "FTIFax");
        if ((legacyRoot is null && DataRoot != DefaultDataRoot) || !Directory.Exists(legacy) || File.Exists(DatabasePath)) return;
        Directory.CreateDirectory(DataRoot);
        foreach (var entry in Directory.EnumerateFileSystemEntries(legacy))
        {
            var target = Path.Combine(DataRoot, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                if (!Directory.Exists(target)) Directory.Move(entry, target);
                else MergeDirectory(entry, target);
            }
            else if (!File.Exists(target)) File.Move(entry, target);
        }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var oldDb = Path.Combine(DataRoot, "ftifax.db" + suffix);
            if (File.Exists(oldDb)) File.Move(oldDb, DatabasePath + suffix, overwrite: true);
        }
        try { Directory.Delete(legacy, recursive: false); } catch (IOException) { /* not empty: leave it */ }
    }

    private static void MergeDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!File.Exists(dest)) File.Move(file, dest);
        }
    }

    public void SetStorageRoot(string? storageRoot) =>
        StorageRoot = string.IsNullOrWhiteSpace(storageRoot) ? Path.Combine(DataRoot, "Storage") : storageRoot;

    public string JobDirectory(Guid jobId) => Path.Combine(JobsDirectory, jobId.ToString("N"));

    /// <summary>Resolves a stored path: relative paths are relative to the storage root.</summary>
    public string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.Combine(StorageRoot, path);

    public string MakeRelative(string fullPath)
    {
        var rel = Path.GetRelativePath(StorageRoot, fullPath);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? fullPath : rel;
    }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { DataRoot, LogsDirectory, ProtocolLogsDirectory, StorageRoot, JobsDirectory, InboxDirectory, FailedReceptionsDirectory })
            Directory.CreateDirectory(dir);
    }
}
