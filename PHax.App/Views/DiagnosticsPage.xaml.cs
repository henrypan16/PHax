using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using PHax.App.Services;
using PHax.App.ViewModels;
using PHax.Core.Ipc;
using PHax.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PHax.App.Views;

public sealed partial class DiagnosticsPage : Page
{
    private const int MaxLines = 2000;
    private readonly ShellViewModel _shell = App.GetService<ShellViewModel>();
    private readonly ServiceClient _client = App.GetService<ServiceClient>();

    public ObservableCollection<DiagnosticLine> AtLines { get; } = [];
    public ObservableCollection<DiagnosticLine> EventLines { get; } = [];

    public DiagnosticsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _shell.ProtocolEvent += OnEvent;
            _shell.PropertyChanged += OnShellChanged;
            await RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            _shell.ProtocolEvent -= OnEvent;
            _shell.PropertyChanged -= OnShellChanged;
        };
    }

    private void OnShellChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Status)) RenderStatus(_shell.Status);
    }

    private void OnEvent(DiagnosticEntryDto e)
    {
        if (Follow.IsChecked != true) return;
        Add(e.Category is "AT>" or "AT<" or "DATA" ? AtLines : EventLines, DiagnosticLine.From(e), scroll: true);
    }

    private void Add(ObservableCollection<DiagnosticLine> list, DiagnosticLine line, bool scroll)
    {
        list.Add(line);
        while (list.Count > MaxLines) list.RemoveAt(0);
        if (scroll) (list == AtLines ? AtList : EventList).ScrollIntoView(line);
    }

    private async Task RefreshAsync()
    {
        RenderStatus(_shell.Status);
        if (!_client.IsConnected) return;
        try
        {
            var snap = await _client.RequestAsync<DiagnosticsSnapshot>(PipeCommands.GetDiagnostics);
            if (snap is null) return;
            RenderStatus(snap.Status);
            AtLines.Clear();
            EventLines.Clear();
            foreach (var e in snap.AtHistory) Add(AtLines, DiagnosticLine.From(e), false);
            foreach (var e in snap.ProtocolEvents) Add(EventLines, DiagnosticLine.From(e), false);
            if (AtLines.Count > 0) AtList.ScrollIntoView(AtLines[^1]);
            if (EventLines.Count > 0) EventList.ScrollIntoView(EventLines[^1]);
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void RenderStatus(ServiceStatusDto? s)
    {
        StateText.Text = s is null ? "Service not connected" : s.ModemState + (s.IsSimulated ? " (SIMULATOR)" : "");
        PortText.Text = s is null ? "" : $"{s.PortName} {s.ModemIdentity}";
        ClassText.Text = s?.FaxClass is { } c ? $"Class {c}" : "—";
        PhaseText.Text = s?.Phase.ToString() ?? "—";
        ModText.Text = s?.Modulation ?? "—";
        ResText.Text = s?.Resolution?.ToString() ?? "—";
        ErrorText.Text = s?.LastError ?? "";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var path = await Pickers.SaveFileAsync(App.Shell, $"PHax-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}", "Text file", ".txt");
        if (path is null) return;
        var sb = new StringBuilder();
        var s = _shell.Status;
        sb.AppendLine($"PHax diagnostics exported {DateTime.Now:O}");
        sb.AppendLine($"Modem state: {s?.ModemState}; port {s?.PortName}; identity {s?.ModemIdentity}; class {s?.FaxClass}; simulated {s?.IsSimulated}");
        sb.AppendLine($"Last error: {s?.LastError}");
        sb.AppendLine("Document contents are not included in this export.");
        sb.AppendLine().AppendLine("== AT command history ==");
        foreach (var l in AtLines) sb.AppendLine($"{l.Time} {l.Category} {l.Message}");
        sb.AppendLine().AppendLine("== Protocol events ==");
        foreach (var l in EventLines) sb.AppendLine($"{l.Time} [{l.Category}] {l.Message}");
        await File.WriteAllTextAsync(path, sb.ToString());
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var dir = App.GetService<AppPaths>().LogsDirectory;
        if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}
