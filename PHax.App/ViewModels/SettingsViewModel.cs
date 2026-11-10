using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PHax.App.Services;
using PHax.Core.Integrations;
using PHax.Core.Ipc;
using PHax.Core.Models;
using PHax.Core.Remote;
using PHax.Core.Updates;
using PHax.Data;
using PHax.Modem.Profiles;
using PHax.Modem.Serial;

namespace PHax.App.ViewModels;

public sealed record PortChoice(string PortName, string Label);

/// <summary>One installed integration on the Integrations tab; the check box edits the settings being edited.</summary>
public sealed class IntegrationItem(IAppIntegration integration, IntegrationSettings settings)
{
    public string Title => integration.Title;
    public string Description => integration.Description;
    public string EnabledLabel => integration.EnabledLabel;
    public string Status => $"Status: {integration.Status}";
    public bool Enabled
    {
        get => settings.GetBool(integration.EnabledSetting, true);
        set => settings.SetBool(integration.EnabledSetting, value);
    }
}

/// <summary>A PC paired with this server, on the Network tab.</summary>
public sealed class PairedClientItem(PairedClientDto client)
{
    public Guid Id { get; } = client.Id;
    public string Name { get; } = client.Name;
    public string Detail { get; } = client.LastSeenUtc is { } seen
        ? $"Last connected {seen.ToLocalTime():g}{(client.LastAddress is null ? "" : $" from {client.LastAddress}")}"
        : "Has not connected yet";
}

/// <summary>Settings editor. Numeric settings are exposed as doubles for NumberBox two-way binding.</summary>
public sealed partial class SettingsViewModel(
    SettingsStore store,
    ServiceClient client,
    IEnumerable<IAppIntegration> integrations,
    NetworkConfigStore networkStore,
    NetworkConfig running,
    UpdateService updates,
    IntegrationLoadResult integrationLoad) : ObservableObject
{
    public static readonly int[] BaudRates = [9600, 19200, 38400, 57600, 115200];
    public static readonly int[] Speeds = [14400, 12000, 9600, 7200, 4800, 2400];

    [ObservableProperty] public partial AppSettings Settings { get; set; } = new();
    // Modem test result: a one-line summary, with the full report behind "More details".
    [ObservableProperty] public partial bool ProbeVisible { get; set; }
    [ObservableProperty] public partial string ProbeTitle { get; set; } = "";
    [ObservableProperty] public partial string ProbeSummary { get; set; } = "";
    [ObservableProperty] public partial string ProbeDetails { get; set; } = "";
    [ObservableProperty] public partial bool ProbeWarning { get; set; }
    [ObservableProperty] public partial bool ShowProbeDetails { get; set; }
    [ObservableProperty] public partial string PrinterNote { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string SaveMessage { get; set; } = "";

    public ObservableCollection<PortChoice> Ports { get; } = [];

    /// <summary>The Integrations tab is shown for loaded add-ons, and for add-ons that failed to load so the reason is visible.</summary>
    public bool HasIntegrations { get; } = integrations.Any() || integrationLoad.Problems.Count > 0;
    public string IntegrationProblems { get; } = string.Join("\n", integrationLoad.Problems);
    [ObservableProperty] public partial IReadOnlyList<IntegrationItem> Integrations { get; set; } = [];

    /// <summary>The role the app is running in. A saved change takes effect after a restart.</summary>
    public bool IsClient { get; } = running.Mode == NetworkMode.Client;

    [ObservableProperty] public partial int ModeIndex { get; set; }
    public bool ServerSelected => ModeIndex == 0;
    public bool ClientSelected => ModeIndex == 1;
    [ObservableProperty] public partial bool AllowClients { get; set; }
    [ObservableProperty] public partial double NetworkPort { get; set; } = NetworkConfig.DefaultPort;
    [ObservableProperty] public partial string ServerAddressText { get; set; } = "";
    [ObservableProperty] public partial string PairingCode { get; set; } = "";
    [ObservableProperty] public partial string NetworkStatus { get; set; } = "";
    [ObservableProperty] public partial string NewCodeText { get; set; } = "";
    [ObservableProperty] public partial bool IsPairing { get; set; }
    public ObservableCollection<PairedClientItem> PairedClients { get; } = [];

    partial void OnModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ServerSelected));
        OnPropertyChanged(nameof(ClientSelected));
    }

    /// <summary>True when the saved network role differs from what the running app was started with.</summary>
    public bool RestartRequired
    {
        get
        {
            var saved = networkStore.Load();
            return saved.Mode != running.Mode || (saved.Mode == NetworkMode.Client
                && (saved.ServerAddress != running.ServerAddress || saved.ServerThumbprint != running.ServerThumbprint || saved.ProtectedToken != running.ProtectedToken));
        }
    }

    private void LoadNetwork()
    {
        var config = networkStore.Load();
        ModeIndex = config.Mode == NetworkMode.Client ? 1 : 0;
        AllowClients = config.AllowClients;
        NetworkPort = config.Port;
        ServerAddressText = config.ServerAddress;
        NetworkStatus = config.Mode != NetworkMode.Client ? ""
            : config.IsPaired ? $"Connected to the fax server at {config.ServerAddress}."
            : "This PC is not connected to a fax server yet.";
    }

    private void SaveNetwork()
    {
        var address = ServerAddressText.Trim();
        if (ClientSelected && address.Length > 0 && ServerAddress.ToBaseUri(address) is null)
            throw new InvalidOperationException($"\"{address}\" is not a valid server address.");
        var config = networkStore.Load();
        config.Mode = ClientSelected ? NetworkMode.Client : NetworkMode.Server;
        config.AllowClients = AllowClients;
        config.Port = double.IsNaN(NetworkPort) ? NetworkConfig.DefaultPort : (int)NetworkPort;
        config.ServerAddress = address;
        networkStore.Save(config);
    }

    public async Task RefreshClientsAsync()
    {
        PairedClients.Clear();
        if (!client.IsConnected || IsClient) return;
        try
        {
            foreach (var c in await client.RequestAsync<List<PairedClientDto>>(PipeCommands.ListClients) ?? []) PairedClients.Add(new PairedClientItem(c));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { NewCodeText = ex.Message; }
    }

    /// <summary>Saves the settings (the listener may need to start) and shows a one-time code for the next PC.</summary>
    public async Task CreatePairingCodeAsync()
    {
        AllowClients = true;
        await SaveAsync();
        if (!client.IsConnected) { NewCodeText = "The PHax service is not running."; return; }
        try
        {
            var code = await client.RequestAsync<PairingCodeDto>(PipeCommands.CreatePairingCode, null, TimeSpan.FromSeconds(15));
            if (code is null) return;
            var suffix = code.Port == NetworkConfig.DefaultPort ? "" : $":{code.Port}";
            NewCodeText = $"Pairing code: {code.Code}\nWorks once, until {code.ExpiresUtc.ToLocalTime():t}. A new code replaces this one.\n" +
                          $"Server address: {string.Join(" or ", code.Addresses.Select(a => a + suffix))}";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { NewCodeText = ex.Message; }
    }

    public async Task RemoveClientAsync(PairedClientItem item)
    {
        await client.SendAsync(PipeCommands.RemoveClient, new ClientRef(item.Id));
        await RefreshClientsAsync();
    }

    /// <summary>Pairs with the server. Other settings are saved first, because pairing also switches this PC to client mode.</summary>
    public async Task ConnectAsync()
    {
        IsPairing = true;
        NetworkStatus = "Connecting…";
        try
        {
            ModeIndex = 1;
            await SaveAsync();
            await networkStore.PairAsync(ServerAddressText, PairingCode);
            PairingCode = "";
            NetworkStatus = $"Connected to the fax server at {ServerAddressText.Trim()}.";
        }
        catch (Exception ex) when (ex is FaxServerException or InvalidOperationException) { NetworkStatus = ex.Message; }
        finally { IsPairing = false; }
    }

    private const string DefaultPrinterLabel = "(Default printer)";
    public ObservableCollection<string> Printers { get; } = [];

    /// <summary>Index into <see cref="Printers"/>; 0 = default printer (stored as an empty name).</summary>
    public int PrinterIndex
    {
        get
        {
            var name = Settings.Incoming.AutoPrintPrinter;
            return string.IsNullOrEmpty(name) ? 0 : Math.Max(0, Printers.IndexOf(Printers.FirstOrDefault(p => p == name || p.StartsWith(name + " (")) ?? ""));
        }
        set
        {
            if (value < 0 || value >= Printers.Count) return;
            var label = Printers[value];
            Settings.Incoming.AutoPrintPrinter = value == 0 ? "" : label.EndsWith(" (not available)") ? label[..^16] : label;
        }
    }

    /// <summary>Lists the printers the service can use; falls back to this user's printers when the service is not running.</summary>
    public async Task RefreshPrintersAsync()
    {
        IReadOnlyList<string> names;
        if (client.IsConnected)
        {
            try
            {
                names = await client.RequestAsync<List<string>>(PipeCommands.ListPrinters) ?? [];
                PrinterNote = "Printers available to the PHax service. Printing is done by the service, so it works even when this window is closed.";
            }
            catch (Exception ex)
            {
                names = Imaging.Conversion.FaxPrinter.InstalledPrinters();
                PrinterNote = $"Could not ask the service for its printers ({ex.Message}); showing this user's printers.";
            }
        }
        else
        {
            names = Imaging.Conversion.FaxPrinter.InstalledPrinters();
            PrinterNote = "The PHax service is not running; showing this user's printers. Printers installed for only one user may not work for automatic printing.";
        }
        Printers.Clear();
        Printers.Add(DefaultPrinterLabel);
        foreach (var n in names) Printers.Add(n);
        var saved = Settings.Incoming.AutoPrintPrinter;
        if (!string.IsNullOrEmpty(saved) && !names.Contains(saved)) Printers.Add($"{saved} (not available)");
        OnPropertyChanged(nameof(PrinterIndex));
    }

    public IReadOnlyList<ModemProfile> Profiles => ModemProfiles.All;
    public IReadOnlyList<string> BaudRateLabels { get; } = BaudRates.Select(b => b.ToString()).ToList();
    public IReadOnlyList<string> SpeedLabels { get; } = Speeds.Select(s => $"{s} bit/s").ToList();

    public double AnswerAfterRings { get => Settings.Incoming.AnswerAfterRings; set => Settings.Incoming.AnswerAfterRings = (int)Math.Clamp(value, 1, 10); }
    public double TransmissionTimeout { get => Settings.Outgoing.TransmissionTimeoutSeconds; set => Settings.Outgoing.TransmissionTimeoutSeconds = (int)Math.Max(60, value); }
    public double MaxAttempts { get => Settings.Redial.MaxAttempts; set => Settings.Redial.MaxAttempts = (int)Math.Clamp(value, 1, 20); }
    public double DelayMinutes { get => Settings.Redial.DelayMinutes; set => Settings.Redial.DelayMinutes = (int)Math.Clamp(value, 1, 240); }
    public double BackupInterval { get => Settings.Backup.ScheduleIntervalHours; set => Settings.Backup.ScheduleIntervalHours = (int)Math.Clamp(value, 1, 24 * 30); }
    public double KeepBackups { get => Settings.Backup.KeepBackups; set => Settings.Backup.KeepBackups = (int)Math.Clamp(value, 1, 365); }

    public int BaudIndex { get => Math.Max(0, Array.IndexOf(BaudRates, Settings.Modem.BaudRate)); set { if (value >= 0) Settings.Modem.BaudRate = BaudRates[value]; } }
    public int SpeedIndex { get => Math.Max(0, Array.IndexOf(Speeds, Settings.Outgoing.MaxSpeed)); set { if (value >= 0) Settings.Outgoing.MaxSpeed = Speeds[value]; } }
    public int ProfileIndex
    {
        get => Math.Max(0, ModemProfiles.All.ToList().FindIndex(p => p.Id == Settings.Modem.ProfileId));
        set { if (value >= 0) { Settings.Modem.ProfileId = ModemProfiles.All[value].Id; OnPropertyChanged(nameof(ProfileNotes)); } }
    }
    public string ProfileNotes => ModemProfiles.Get(Settings.Modem.ProfileId) is var p ? $"{p.Description} {p.CompatibilityNotes}" : "";

    public PortChoice? SelectedPort
    {
        get => Ports.FirstOrDefault(p => p.PortName == Settings.Modem.PortName);
        set { if (value is not null) Settings.Modem.PortName = value.PortName; }
    }

    public async Task LoadAsync()
    {
        Settings = await store.LoadAsync();
        Integrations = integrations.Select(i => new IntegrationItem(i, Settings.Integration)).ToList();
        LoadNetwork();
        await RefreshClientsAsync();
        RefreshPorts();
        await RefreshPrintersAsync();
        OnPropertyChanged(string.Empty);
    }

    public void RefreshPorts()
    {
        Ports.Clear();
        foreach (var p in SerialPortDiscovery.List()) Ports.Add(new PortChoice(p.PortName, p.ToString()));
        if (!string.IsNullOrEmpty(Settings.Modem.PortName) && Ports.All(p => p.PortName != Settings.Modem.PortName))
            Ports.Add(new PortChoice(Settings.Modem.PortName, $"{Settings.Modem.PortName} (not present)"));
        OnPropertyChanged(nameof(SelectedPort));
    }

    /// <summary>Saves the edited settings, keeping scan choices the Scan dialog may have saved since this page loaded.</summary>
    private async Task SaveSettingsAsync()
    {
        Settings.Scanning = (await store.LoadAsync()).Scanning;
        await store.SaveAsync(Settings);
    }

    public async Task SaveAsync()
    {
        SaveNetwork();
        await SaveSettingsAsync();
        try { StartupRegistration.Apply(Settings.General.StartWithWindows, Settings.General.StartMinimized); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { }
        bool told = await client.TrySendAsync(PipeCommands.ReloadSettings);
        SaveMessage = told ? $"Saved at {DateTime.Now:t}. The service reloaded the settings." : $"Saved at {DateTime.Now:t}. The service is not running; settings apply when it starts.";
    }

    private void ShowProbe(bool ok, string title, string summary, string details = "")
    {
        ProbeVisible = true;
        ProbeWarning = !ok;
        ProbeTitle = title;
        ProbeSummary = summary;
        ProbeDetails = details;
        ShowProbeDetails = false;
    }

    public async Task ProbeAsync()
    {
        var port = Settings.Modem.PortName;
        if (string.IsNullOrEmpty(port)) { ShowProbe(false, "Modem test failed", "Select a COM port first."); return; }
        if (!client.IsConnected) { ShowProbe(false, "Modem test failed", "The PHax service is not running. Only the service may open the modem port."); return; }
        IsBusy = true;
        ShowProbe(true, $"Testing {port}…", "This takes a few seconds.");
        ProbeWarning = false;
        try
        {
            var r = await client.RequestAsync<ModemProbeResult>(PipeCommands.ProbeModem,
                new ModemProbeRequest(port, Settings.Modem.BaudRate, Settings.Modem.HardwareFlowControl, Settings.Modem.ProfileId),
                TimeSpan.FromSeconds(75));
            if (r is null) { ShowProbe(false, "Modem test failed", "The service returned no result."); return; }

            var lines = new List<string>
            {
                !r.Opened ? $"Could not open the port: {r.Error}"
                : !r.Responds ? $"Port opened but the modem did not respond. {r.Error}"
                : r.SupportsClass1 ? "Class 1 fax modem detected and initialised."
                : $"UNSUPPORTED MODEM: {r.Error}",
            };
            if (r.Identity is { Length: > 0 }) lines.Add($"Identity: {r.Identity}");
            if (r.FaxClasses.Count > 0) lines.Add($"Fax classes: {string.Join(", ", r.FaxClasses)}");
            if (r.TransmitModulations.Count > 0) lines.Add($"+FTM: {string.Join(",", r.TransmitModulations)}   +FRM: {string.Join(",", r.ReceiveModulations)}");
            var suggested = ModemProfiles.Suggest(r.Identity);
            if (r.Responds && suggested.Id != Settings.Modem.ProfileId && suggested != ModemProfiles.GenericClass1) lines.Add($"Suggested profile: {suggested.DisplayName}");
            lines.Add("Note: a successful test shows Class 1 command support only. Send and receive a real fax to confirm compatibility.");
            var details = string.Join(Environment.NewLine, lines);
            if (r.Transcript.Count > 0) details += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, r.Transcript.TakeLast(30));

            var classes = string.Join(" / ", r.FaxClasses.Where(c => c is "1" or "1.0").Select(c => $"Class {c}"));
            var summary = !r.Opened ? $"Could not open {port}."
                : !r.Responds ? $"The modem on {port} did not respond."
                : r.SupportsClass1 ? $"{r.Identity} on {port} supports {classes}."
                : $"The modem on {port} does not support Fax Class 1. PHax cannot use it.";
            ShowProbe(r.SupportsClass1, r.SupportsClass1 ? "Modem test passed" : "Modem test failed", summary, details);
        }
        catch (Exception ex)
        {
            ShowProbe(false, "Modem test failed", "The test did not complete.", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<string> ResetModemAsync() =>
        await client.TrySendAsync(PipeCommands.ResetModem) ? "Modem reset requested." : "The PHax service is not running.";

    public async Task<string> BackupNowAsync()
    {
        await SaveSettingsAsync();
        if (!client.IsConnected) return "The PHax service is not running.";
        var r = await client.RequestAsync<BackupRecord>(PipeCommands.RunBackup, null, TimeSpan.FromMinutes(10));
        return r is { Success: true } ? $"Backup written to {r.Path}" : $"Backup failed: {r?.Message}";
    }

    public async Task<string> RestoreAsync(string path)
    {
        if (!client.IsConnected) return "The PHax service is not running.";
        await client.SendAsync(PipeCommands.RestoreBackup, new RestoreRequest(path), TimeSpan.FromMinutes(30));
        await LoadAsync();
        return "Backup restored.";
    }

    // ---- Updates (General tab) ----

    public string VersionLabel => updates.VersionLabel;
    [ObservableProperty] public partial string UpdateStatus { get; set; } = "";
    [ObservableProperty] public partial bool UpdateBusy { get; set; }
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial double DownloadProgress { get; set; }
    [ObservableProperty] public partial UpdateInfo? AvailableUpdate { get; set; }
    public bool HasUpdate => AvailableUpdate is not null;
    public Uri? ReleaseNotesUri => AvailableUpdate is { ReleaseUrl.Length: > 0 } u && Uri.TryCreate(u.ReleaseUrl, UriKind.Absolute, out var uri) ? uri : null;
    private (UpdateInfo Update, string Path)? _downloaded;

    partial void OnAvailableUpdateChanged(UpdateInfo? value)
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ReleaseNotesUri));
    }

    public async Task CheckForUpdatesAsync()
    {
        UpdateBusy = true;
        UpdateStatus = "Checking GitHub for updates…";
        try
        {
            var result = await updates.CheckAsync();
            AvailableUpdate = result.Update;
            UpdateStatus = result.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AvailableUpdate = null;
            UpdateStatus = $"Could not check for updates: {(ex is TaskCanceledException ? "GitHub did not respond." : ex.Message)}";
        }
        finally { UpdateBusy = false; }
    }

    /// <summary>
    /// Why updating now would interrupt a fax, or null. The installer stops the service, which would cut off a call in
    /// progress. Queued faxes are safe: they wait in the database. A client PC's own service has no modem.
    /// </summary>
    public async Task<string?> GetUpdateBlockerAsync()
    {
        if (IsClient || !client.IsConnected) return null;
        try
        {
            var status = await client.RequestAsync<ServiceStatusDto>(PipeCommands.GetStatus);
            return status?.ModemState is ModemState.Sending or ModemState.Receiving or ModemState.Ringing
                ? "A fax is being sent or received right now. Update when the call has finished."
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { return null; }
    }

    /// <summary>
    /// Downloads (once per version) and runs the installer, reporting each stage in <see cref="UpdateStatus"/>.
    /// Normally the installer closes PHax and this never returns. A returned message means the update did not happen;
    /// null means the user cancelled the download.
    /// </summary>
    public async Task<string?> DownloadAndInstallAsync(CancellationToken ct)
    {
        if (AvailableUpdate is not { } update) return "Check for updates first.";
        UpdateBusy = true;
        try
        {
            if (_downloaded is not { } d || d.Update != update || !File.Exists(d.Path))
            {
                IsDownloading = true;
                DownloadProgress = 0;
                UpdateStatus = $"Downloading {update.ReleaseName} ({update.Size / 1_048_576.0:0} MB)…";
                var progress = new Progress<double>(p =>
                {
                    DownloadProgress = p;
                    UpdateStatus = $"Downloading {update.ReleaseName}… {p:P0}";
                });
                _downloaded = (update, await updates.DownloadAsync(update, progress, ct));
                IsDownloading = false;
            }
            // The download can take minutes; a call may have started meanwhile.
            if (await GetUpdateBlockerAsync() is { } blocker) { UpdateStatus = blocker; return blocker; }

            UpdateStatus = "Starting the installer… Windows checks it first, which can take a little while.";
            using var installer = await updates.StartInstallerAsync(_downloaded.Value.Path);
            UpdateStatus = "Installing… If Windows asks for administrator permission, choose Yes. PHax closes and reopens by itself when the update is done.";
            await installer.WaitForExitAsync();
            int code = installer.ExitCode;
            UpdateStatus = code == 0
                ? "The update was installed. Restart PHax to use the new version."
                : $"The update was not installed (installer exit code {code}). If Windows asked for administrator permission, it has to be allowed.";
            return UpdateStatus;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            UpdateStatus = "Update cancelled.";
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                       or TaskCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UpdateStatus = $"Update failed: {ex.Message}";
            return UpdateStatus;
        }
        finally
        {
            IsDownloading = false;
            UpdateBusy = false;
        }
    }
}
