using PHax.App.Services;
using PHax.App.ViewModels;
using PHax.Data;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace PHax.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly TrayIcon _tray;
    private bool _exiting;

    public ShellViewModel Shell { get; } = App.GetService<ShellViewModel>();
    public IntPtr Hwnd { get; }

    public MainWindow()
    {
        InitializeComponent();
        UiThread.Queue = DispatcherQueue;
        Hwnd = WindowNative.GetWindowHandle(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        AppIcon.Apply(AppWindow);
        ExtendsContentIntoTitleBar = false;

        _tray = new TrayIcon(Hwnd);
        _tray.OpenRequested += () => UiThread.Post(ShowFromTray);
        _tray.NewFaxRequested += () => UiThread.Post(() => { ShowFromTray(); ComposeWindow.Open(); });
        _tray.ExitRequested += () => UiThread.Post(async () => await ExitAsync());
        _tray.Show("PHax");

        AppWindow.Closing += OnClosing;
        Nav.Loaded += async (_, _) =>
        {
            Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == "inbox");
            await Shell.RefreshUnreadAsync();
        };
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;
        var settings = await App.GetService<SettingsStore>().LoadAsync();
        if (settings.General.MinimizeToTray)
        {
            args.Cancel = true;
            AppWindow.Hide();
            return;
        }
        args.Cancel = true;
        await ExitAsync();
    }

    private void ShowFromTray() => BringToFront();

    /// <summary>Shows, restores and focuses the window (tray click or a second launch of PHax).</summary>
    public void BringToFront()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
        SetForegroundWindow(Hwnd);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    private async Task ExitAsync()
    {
        _exiting = true;
        _tray.Dispose();
        await App.ShutdownAsync();
    }

    public void ShowStartupError(string message)
    {
        StartupError.Message = message;
        StartupError.IsOpen = true;
    }

    public void NavigateTo(string tag)
    {
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null) Nav.SelectedItem = item;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = args.IsSettingsSelected ? typeof(SettingsPage) : (args.SelectedItem as NavigationViewItem)?.Tag switch
        {
            "incoming" => typeof(IncomingPage),
            "inbox" => typeof(InboxPage),
            "drafts" => typeof(DraftsPage),
            "outbox" => typeof(OutboxPage),
            "sent" => typeof(SentPage),
            _ => null,
        };
        if (page is not null && ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
    }

    private void NewFax_Click(object sender, RoutedEventArgs e) => ComposeWindow.Open();
}
