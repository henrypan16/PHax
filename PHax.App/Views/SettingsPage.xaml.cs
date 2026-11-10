using PHax.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PHax.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; } = App.GetService<SettingsViewModel>();

    public SettingsPage()
    {
        InitializeComponent();
        if (!ViewModel.HasIntegrations) IntegrationsTabItem.Visibility = Visibility.Collapsed;
        // A client PC has no modem of its own; those settings live on the server. Removed rather than collapsed:
        // collapsing the initially selected item leaves SelectorBar showing no tabs at all.
        if (ViewModel.IsClient)
            foreach (var item in Tabs.Items.Where(i => (string)i.Tag is "modem" or "incoming" or "outgoing" or "redial" or "backup").ToList())
                Tabs.Items.Remove(item);
        _pickerTabs = Tabs.Items.Where(i => i.Visibility == Visibility.Visible).ToList();
        foreach (var item in _pickerTabs) TabPicker.Items.Add(item.Text);
        Loaded += async (_, _) =>
        {
            await ViewModel.LoadAsync();
            Bindings.Update();
        };
    }

    public static InfoBarSeverity ProbeSeverity(bool warning, bool busy) =>
        busy ? InfoBarSeverity.Informational : warning ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public static string DetailsLabel(bool showing) => showing ? "Hide details" : "More details";

    private void ProbeDetails_Click(object sender, RoutedEventArgs e) => ViewModel.ShowProbeDetails = !ViewModel.ShowProbeDetails;

    private void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        var error = App.GetService<Services.AppNotifications>().Show("PHax test notification",
            "Notifications are working. You will be told when faxes are received, sent or fail.", ViewModel.Settings.Notifications.PlaySound);
        TestNotificationResult.Text = error is null
            ? "Sent. If nothing appeared, check Windows Settings > System > Notifications (PHax and Do not disturb)."
            : $"Could not show the notification: {error}";
    }

    private void ProbeInfo_Closed(InfoBar sender, object args) => ViewModel.ProbeVisible = false;

    private async void RefreshPrinters_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshPrintersAsync();
        Bindings.Update();
    }

    /// <summary>Remembered for the session so returning to Settings reopens the last tab.</summary>
    private static string _lastTab = "modem";

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var visible = Tabs.Items.Where(i => i.Visibility == Visibility.Visible).ToList();
        var item = visible.FirstOrDefault(i => (string)i.Tag == _lastTab) ?? visible[0];
        Tabs.SelectedItem = item;
        ShowTab((string)item.Tag);
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is not string tag) return;
        ShowTab(tag);
        if (sender.SelectedItem is { } selected) TabPicker.SelectedIndex = _pickerTabs.IndexOf(selected);
    }

    /// <summary>The tabs in the order the narrow-window dropdown lists them.</summary>
    private readonly List<SelectorBarItem> _pickerTabs;

    /// <summary>Width the tab bar needs to show every tab; measured while it is visible.</summary>
    private double _tabsWidth;

    private void TabPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Goes through the SelectorBar so both controls always agree; Tabs_SelectionChanged shows the tab.
        if (TabPicker.SelectedIndex is >= 0 and var i && i < _pickerTabs.Count && Tabs.SelectedItem != _pickerTabs[i])
            Tabs.SelectedItem = _pickerTabs[i];
    }

    private void TabRow_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabLayout();

    /// <summary>Shows the tab bar when all its tabs fit, otherwise the dropdown, so every tab stays reachable.</summary>
    private void UpdateTabLayout()
    {
        if (Tabs.Visibility == Visibility.Visible)
        {
            Tabs.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            _tabsWidth = Tabs.DesiredSize.Width;
        }
        if (_tabsWidth <= 0 || TabRow.ActualWidth <= 0) return;
        bool narrow = _tabsWidth > TabRow.ActualWidth;
        Tabs.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        TabPicker.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowTab(string tag)
    {
        _lastTab = tag;
        var panels = new Dictionary<string, StackPanel>
        {
            ["modem"] = ModemTab, ["general"] = GeneralTab, ["network"] = NetworkTab, ["incoming"] = IncomingTab, ["outgoing"] = OutgoingTab,
            ["redial"] = RedialTab, ["integrations"] = IntegrationsTab, ["backup"] = BackupTab, ["notifications"] = NotificationsTab,
        };
        foreach (var (key, panel) in panels) panel.Visibility = key == tag ? Visibility.Visible : Visibility.Collapsed;

        bool diagnostics = tag == "diagnostics";
        TabScroller.Visibility = diagnostics ? Visibility.Collapsed : Visibility.Visible;
        SaveBar.Visibility = diagnostics ? Visibility.Collapsed : Visibility.Visible;
        DiagnosticsFrame.Visibility = diagnostics ? Visibility.Visible : Visibility.Collapsed;
        if (diagnostics && DiagnosticsFrame.Content is null) DiagnosticsFrame.Navigate(typeof(DiagnosticsPage));
        if (!diagnostics) TabScroller.ChangeView(null, 0, null, disableAnimation: true);
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.SaveAsync(); }
        catch (Exception ex)
        {
            await Dialogs.MessageAsync(XamlRoot, "Save settings", ex.Message);
            return;
        }
        await OfferRestartAsync();
    }

    private async Task OfferRestartAsync()
    {
        if (ViewModel.RestartRequired &&
            await Dialogs.ConfirmAsync(XamlRoot, "Restart PHax", "The network change takes effect when PHax restarts. Restart now?", "Restart"))
            await App.RestartAsync();
    }

    private async void AddPc_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.CreatePairingCodeAsync(); }
        catch (Exception ex) { await Dialogs.MessageAsync(XamlRoot, "Add a PC", ex.Message); }
        await ViewModel.RefreshClientsAsync();
    }

    private async void RemovePc_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not PairedClientItem item) return;
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Remove PC", $"Stop {item.Name} from using this fax server? It would need a new pairing code to connect again.", "Remove")) return;
        try { await ViewModel.RemoveClientAsync(item); }
        catch (Exception ex) { await Dialogs.MessageAsync(XamlRoot, "Remove PC", ex.Message); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConnectAsync();
        await OfferRestartAsync();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await ViewModel.CheckForUpdatesAsync();

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.AvailableUpdate is not { } update) return;
        if (await ViewModel.GetUpdateBlockerAsync() is { } blocker)
        {
            await Dialogs.MessageAsync(XamlRoot, "Update PHax", blocker);
            return;
        }
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Update PHax",
                $"{update.ReleaseName} will be downloaded ({update.Size / 1_048_576.0:0} MB) and installed.\n\n" +
                "PHax and its service close while the update installs and PHax reopens when it is done. Faxes waiting in the Outbox are kept " +
                "and sent afterwards. Save any fax you are writing first.\n\nWindows will ask for administrator permission.", "Update"))
            return;
        var message = await RunUpdateWithProgressAsync();
        // Reaching this point means the installer did not take over (it normally closes PHax).
        if (message is not null) await Dialogs.MessageAsync(XamlRoot, "Update PHax", message);
    }

    /// <summary>
    /// Shows a modal window for the whole update, so the user always sees what is happening and is not tempted to
    /// close PHax while the installer starts. It can be cancelled only during the download.
    /// </summary>
    private async Task<string?> RunUpdateWithProgressAsync()
    {
        using var cancel = new CancellationTokenSource();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Preparing the update…" };
        var bar = new ProgressBar { Maximum = 1, IsIndeterminate = true, Margin = new Thickness(0, 16, 0, 0) };
        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Text = "Keep PHax open. It closes by itself when the installer takes over and reopens when the update is done.",
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Updating PHax",
            Content = new StackPanel { MinWidth = 380, Children = { status, bar, note } },
            CloseButtonText = "Cancel",
        };
        bool done = false;
        dialog.Closing += (_, args) =>
        {
            if (done) return;
            args.Cancel = true; // Cancel (or Esc) stops the download; the window closes when that has finished.
            if (ViewModel.IsDownloading) cancel.Cancel();
        };
        void Sync()
        {
            status.Text = ViewModel.UpdateStatus;
            bar.IsIndeterminate = !ViewModel.IsDownloading;
            bar.Value = ViewModel.DownloadProgress;
            dialog.CloseButtonText = ViewModel.IsDownloading && !cancel.IsCancellationRequested ? "Cancel" : "";
        }
        System.ComponentModel.PropertyChangedEventHandler onChanged = (_, _) => Sync();
        ViewModel.PropertyChanged += onChanged;
        var shown = dialog.ShowAsync();
        try
        {
            return await ViewModel.DownloadAndInstallAsync(cancel.Token);
        }
        finally
        {
            ViewModel.PropertyChanged -= onChanged;
            done = true;
            dialog.Hide();
            await shown;
        }
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshPorts();
        Bindings.Update();
    }

    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        // Save first so the service re-opens the modem with the tested configuration afterwards.
        await ViewModel.SaveAsync();
        await ViewModel.ProbeAsync();
    }

    private async void Reset_Click(object sender, RoutedEventArgs e) =>
        await Dialogs.MessageAsync(XamlRoot, "Reset modem", await ViewModel.ResetModemAsync());

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        try { await Dialogs.MessageAsync(XamlRoot, "Backup", await ViewModel.BackupNowAsync()); }
        catch (Exception ex) { await Dialogs.MessageAsync(XamlRoot, "Backup", ex.Message); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var path = await Pickers.OpenFileAsync(App.Shell, ".zip");
        if (path is null) return;
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Restore backup",
                "Restoring replaces the fax database and documents with the backup's contents. The modem is released during the restore. Continue?", "Restore"))
            return;
        try
        {
            await Dialogs.MessageAsync(XamlRoot, "Restore", await ViewModel.RestoreAsync(path));
            Bindings.Update();
        }
        catch (Exception ex) { await Dialogs.MessageAsync(XamlRoot, "Restore", ex.Message); }
    }
}
