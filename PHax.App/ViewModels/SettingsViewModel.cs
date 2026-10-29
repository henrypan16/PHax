using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PHax.App.Services;
using PHax.Core.Integrations;
using PHax.Core.Ipc;
using PHax.Core.Models;
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

/// <summary>Settings editor. Numeric settings are exposed as doubles for NumberBox two-way binding.</summary>
public sealed partial class SettingsViewModel(SettingsStore store, ServiceClient client, IEnumerable<IAppIntegration> integrations) : ObservableObject
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

    public bool HasIntegrations { get; } = integrations.Any();
    [ObservableProperty] public partial IReadOnlyList<IntegrationItem> Integrations { get; set; } = [];

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

    public async Task SaveAsync()
    {
        await store.SaveAsync(Settings);
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
        await store.SaveAsync(Settings);
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
}
