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
        var item = Tabs.Items.FirstOrDefault(i => (string)i.Tag == _lastTab) ?? Tabs.Items[0];
        Tabs.SelectedItem = item;
        ShowTab((string)item.Tag);
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag) ShowTab(tag);
    }

    private void ShowTab(string tag)
    {
        _lastTab = tag;
        var panels = new Dictionary<string, StackPanel>
        {
            ["modem"] = ModemTab, ["general"] = GeneralTab, ["incoming"] = IncomingTab, ["outgoing"] = OutgoingTab,
            ["redial"] = RedialTab,["backup"] = BackupTab, ["notifications"] = NotificationsTab,
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
        catch (Exception ex) { await Dialogs.MessageAsync(XamlRoot, "Save settings", ex.Message); }
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
