using NAPS2.Scan;
using PHax.App.Services;
using PHax.Data;
using PHax.Imaging.Conversion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace PHax.App.Views;

/// <summary>Scans a paper document. On success <see cref="ResultPath"/> is a fax TIFF ready to attach.</summary>
public sealed partial class ScanDialog : ContentDialog
{
    private readonly ScannerService _scanner = App.GetService<ScannerService>();
    private readonly SettingsStore _settings = App.GetService<SettingsStore>();
    private readonly IntPtr _owner;
    private CancellationTokenSource? _scan;
    private bool _done;

    public string? ResultPath { get; private set; }

    /// <param name="owner">Window handle the driver's own dialogs are parented to.</param>
    public ScanDialog(IntPtr owner)
    {
        _owner = owner;
        InitializeComponent();
        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var s = (await _settings.LoadAsync()).Scanning;
        Select(SourceBox, s.PaperSource);
        Select(PageSizeBox, s.PageSize);
        Select(ModeBox, s.Mode);
        NativeUiBox.IsChecked = s.UseScannerDialog;
        await ListScannersAsync(refresh: false, s.Scanner);
    }

    private async Task ListScannersAsync(bool refresh, string? preferred)
    {
        SetBusy(true, "Looking for scanners…");
        try
        {
            var scanners = await _scanner.GetScannersAsync(refresh);
            preferred ??= (ScannerBox.SelectedItem as ScannerItem)?.Key;
            ScannerBox.ItemsSource = scanners;
            ScannerBox.SelectedItem = scanners.FirstOrDefault(i => i.Key == preferred) ?? scanners.FirstOrDefault();
            ScannerBox.PlaceholderText = scanners.Count == 0 ? "No scanners found" : "";
            StatusText.Text = scanners.Count == 0
                ? "No scanner was found. Check that it is on, connected, and that its driver (WIA or TWAIN) is installed."
                : "";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ListScannersAsync(refresh: true, preferred: null);

    private async void Scan_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Stay open while scanning; Hide() once the pages are saved.
        args.Cancel = true;
        if (_scan is not null) return;
        if (ScannerBox.SelectedItem is not ScannerItem scanner) { StatusText.Text = "Choose a scanner."; return; }

        var request = new ScanRequest(scanner, Enum.Parse<PaperSource>(SelectedTag(SourceBox)), SelectedTag(PageSizeBox),
            Enum.Parse<BilevelMode>(SelectedTag(ModeBox)), NativeUiBox.IsChecked == true);
        _scan = new CancellationTokenSource();
        SetBusy(true, "Starting scanner…");
        try
        {
            var pages = await _scanner.ScanAsync(request, _owner, new Progress<string>(m => StatusText.Text = m), _scan.Token);
            if (pages.Count == 0)
            {
                // Empty feeder, or the driver's own dialog was cancelled.
                StatusText.Text = "No pages were scanned.";
                return;
            }
            StatusText.Text = "Saving…";
            ResultPath = await Task.Run(() => ScannerService.SaveScan(pages));
            await RememberAsync(request);
            _done = true;
            Hide();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Scan from {Scanner} failed", scanner);
            StatusText.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _scan.Dispose();
            _scan = null;
            SetBusy(false);
        }
    }

    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        // Cancel/Esc during a scan stops the scanner first rather than leaving it running behind a closed dialog.
        if (_scan is null || _done) return;
        _scan.Cancel();
        args.Cancel = true;
    }

    private async Task RememberAsync(ScanRequest request)
    {
        try
        {
            var settings = await _settings.LoadAsync();
            settings.Scanning.Scanner = request.Scanner.Key;
            settings.Scanning.PaperSource = request.Source.ToString();
            settings.Scanning.PageSize = request.PageSize;
            settings.Scanning.Mode = request.Mode.ToString();
            settings.Scanning.UseScannerDialog = request.UseScannerDialog;
            await _settings.SaveAsync(settings);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Saving scan settings failed");
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        Ring.IsActive = busy;
        if (status is not null) StatusText.Text = status;
        IsPrimaryButtonEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        ScannerBox.IsEnabled = SourceBox.IsEnabled = PageSizeBox.IsEnabled = ModeBox.IsEnabled = NativeUiBox.IsEnabled = !busy;
    }

    private static string SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? ((ComboBoxItem)box.Items[0]).Tag as string ?? "";

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == tag) ?? box.Items[0];
}
