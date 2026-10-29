using System.Runtime.InteropServices;
using PHax.App.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace PHax.App.Views;

/// <summary>
/// Small always-on-top window centred at the top of the screen showing an ongoing fax transmission.
/// Shown without taking focus and hidden from the taskbar/Alt+Tab.
/// </summary>
public sealed partial class TransmissionPopup : Window
{
    private const int WidthDip = 460, HeightDip = 104;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _closeTimer;

    /// <summary>Raised when the user clicks the close button (hides it for the rest of the transmission).</summary>
    public event Action? Dismissed;

    public TransmissionPopup()
    {
        InitializeComponent();
        AppIcon.Apply(AppWindow);
        AppWindow.IsShownInSwitchers = false;
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        PlaceTopCenter();
    }

    private void PlaceTopCenter()
    {
        double scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        int w = (int)(WidthDip * scale), h = (int)(HeightDip * scale);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (int)(12 * scale), w, h));
    }

    /// <summary>Shows the window without activating it, so typing in other programs is not interrupted.</summary>
    public void ShowQuietly()
    {
        AppWindow.Show(activateWindow: false);
    }

    /// <param name="percent">Null shows an indeterminate bar (dialing, negotiating, receiving).</param>
    public void Update(string title, string detail, double? percent, bool receiving)
    {
        _closeTimer?.Stop();
        StateIcon.Glyph = receiving ? "" : "";
        StateIcon.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        TitleText.Text = title;
        DetailText.Text = detail;
        Bar.Visibility = Visibility.Visible;
        Bar.ShowError = false;
        Bar.IsIndeterminate = percent is null;
        if (percent is { } p) Bar.Value = Math.Clamp(p, 0, 100);
    }

    /// <summary>Shows the outcome, then closes after <paramref name="closeAfter"/>.</summary>
    public void Finish(string title, string detail, bool success, TimeSpan closeAfter)
    {
        StateIcon.Glyph = success ? "" : "";
        StateIcon.Foreground = new SolidColorBrush(success ? Colors.SeaGreen : Colors.IndianRed);
        TitleText.Text = title;
        DetailText.Text = detail;
        Bar.IsIndeterminate = false;
        Bar.Value = success ? 100 : Bar.Value;
        Bar.ShowError = !success;
        _closeTimer = DispatcherQueue.CreateTimer();
        _closeTimer.Interval = closeAfter;
        _closeTimer.IsRepeating = false;
        _closeTimer.Tick += (_, _) => Close();
        _closeTimer.Start();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
        Close();
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
