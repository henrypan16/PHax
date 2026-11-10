using System.Diagnostics;
using PHax.Core.Updates;

namespace PHax.App.Services;

/// <summary>
/// Settings → General → Updates: checks GitHub for a newer PHax installer, downloads it and runs it. The
/// installer (not this app) needs administrator rights, so Windows asks for them when it starts. It stops this app and
/// the service, upgrades in place, and reopens the app because of <c>/RELAUNCH</c>.
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly UpdateChecker _checker = new(Http);

    public Version CurrentVersion { get; } = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public string VersionLabel => $"PHax {UpdateChecker.Format(CurrentVersion)}";

    /// <summary>Add-ons live in their own folder (see IntegrationLoader) and are not touched by the PHax installer.</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await _checker.CheckAsync(CurrentVersion, timeout.Token);
    }

    public Task<string> DownloadAsync(UpdateInfo update, IProgress<double> progress, CancellationToken ct = default) =>
        _checker.DownloadAsync(update, Path.Combine(Path.GetTempPath(), "PHax-Update"), progress, ct);

    /// <summary>
    /// Starts the installer. Normally it ends this process part-way through; if it exits first, it finished or was
    /// stopped without doing so (for example the administrator prompt was declined). It is started without elevation on
    /// purpose: Inno Setup then elevates itself and can reopen PHax as the signed-in user rather than as administrator.
    /// </summary>
    /// <remarks>
    /// Off the UI thread: ShellExecute does not return until Windows has checked the file (SmartScreen, antivirus), which
    /// for an unsigned ~100 MB installer can take many seconds and would freeze the window.
    /// </remarks>
    public Task<Process> StartInstallerAsync(string installerPath) => Task.Run(() =>
        Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
        }) ?? throw new InvalidOperationException("The installer could not be started."));

    private static HttpClient CreateClient()
    {
        // No overall timeout: the installer is ~100 MB and slow lines are common. The check has its own 30 s limit.
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // GitHub's API requires a User-Agent; keep it generic.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"PHax-Updater/{typeof(UpdateService).Assembly.GetName().Version}");
        return http;
    }
}
