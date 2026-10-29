using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace PHax.App.Services;

/// <summary>Window, taskbar and tray icon generated from logo.png (Assets/PHax.ico).</summary>
public static class AppIcon
{
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "PHax.ico");

    public static void Apply(Microsoft.UI.Windowing.AppWindow window)
    {
        if (File.Exists(IconPath)) window.SetIcon(IconPath);
        else window.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.scale-200.png"));
    }
}

public static class UiThread
{
    public static DispatcherQueue? Queue { get; set; }

    public static void Post(Action action)
    {
        if (Queue is null || Queue.HasThreadAccess) action();
        else Queue.TryEnqueue(() => action());
    }
}

/// <summary>
/// Windows toast notifications for an unpackaged app. The app ID is registered under
/// HKCU\Software\Classes\AppUserModelId (display name and icon) and the process is given that ID, then classic
/// ToastNotificationManager toasts are used. The Windows App SDK AppNotificationManager was unreliable for this
/// self-contained, unpackaged app (it silently failed to register).
/// </summary>
public sealed class AppNotifications
{
    public const string AppUserModelId = "PHax.App";
    private Windows.UI.Notifications.ToastNotifier? _notifier;

    public string? LastError { get; private set; }

    public void Register()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\FTIFax.App", throwOnMissingSubKey: false); // pre-rename ID
            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}"))
            {
                key.SetValue("DisplayName", "PHax");
                var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "PHax.png");
                if (File.Exists(icon)) key.SetValue("IconUri", icon);
            }
            _notifier = Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Serilog.Log.Warning(ex, "Toast notifications could not be registered");
        }
    }

    /// <summary>Shows a toast. Returns null on success, otherwise the reason it could not be shown.</summary>
    public string? Show(string title, string message, bool sound)
    {
        if (_notifier is null) return LastError ?? "Notifications are not registered.";
        try
        {
            var blocked = BlockedReason();
            if (blocked is not null) return blocked;
            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml(
                "<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{System.Security.SecurityElement.Escape(title)}</text>" +
                $"<text>{System.Security.SecurityElement.Escape(message)}</text>" +
                "</binding></visual>" + (sound ? "" : "<audio silent=\"true\"/>") + "</toast>");
            _notifier.Show(new Windows.UI.Notifications.ToastNotification(xml));
            return null;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Toast notification failed");
            return ex.Message;
        }
    }

    /// <summary>Explains why Windows would hide PHax notifications, or null if they are allowed.</summary>
    public string? BlockedReason()
    {
        try
        {
            return _notifier?.Setting switch
            {
                Windows.UI.Notifications.NotificationSetting.DisabledForApplication =>
                    "Notifications for PHax are turned off in Windows Settings > System > Notifications.",
                Windows.UI.Notifications.NotificationSetting.DisabledForUser =>
                    "Notifications are turned off for this Windows user (Settings > System > Notifications).",
                Windows.UI.Notifications.NotificationSetting.DisabledByGroupPolicy => "Notifications are disabled by group policy.",
                Windows.UI.Notifications.NotificationSetting.DisabledByManifest => "Notifications are disabled for this app.",
                _ => null,
            };
        }
        catch (Exception)
        {
            return null; // Setting is unavailable on some builds; try to show anyway.
        }
    }

    public void Unregister() { }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}

/// <summary>"Start with Windows" via the per-user Run key.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PHax";

    public static void Apply(bool enabled, bool minimized)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        key.DeleteValue("FTIFax", throwOnMissingValue: false); // entry written before the rename to PHax
        if (enabled)
        {
            var exe = Environment.ProcessPath!;
            key.SetValue(Name, $"\"{exe}\"{(minimized ? " --minimized" : "")}");
        }
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}

/// <summary>Notification-area icon using Shell_NotifyIcon, with a context menu (Open, New fax, Exit).</summary>
public sealed partial class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 1;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_COMMAND = 0x0111;
    private const int NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const int IdOpen = 1, IdNew = 2, IdExit = 3;

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc;
    private bool _added;

    public event Action? OpenRequested;
    public event Action? NewFaxRequested;
    public event Action? ExitRequested;

    public TrayIcon(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = WndProc;
        SetWindowSubclass(hwnd, _proc, 1, IntPtr.Zero);
    }

    public void Show(string tooltip)
    {
        if (_added) return;
        var data = CreateData(tooltip);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private NOTIFYICONDATA CreateData(string tooltip) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = TrayIconHandle(),
        szTip = tooltip,
    };

    private static IntPtr TrayIconHandle()
    {
        // Small-icon size from PHax.ico; falls back to the stock application icon.
        if (File.Exists(AppIcon.IconPath))
        {
            var handle = LoadImage(IntPtr.Zero, AppIcon.IconPath, 1, GetSystemMetrics(49), GetSystemMetrics(50), 0x10);
            if (handle != IntPtr.Zero) return handle;
        }
        return LoadIcon(IntPtr.Zero, (IntPtr)32512);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        if (msg == WM_APP_TRAY)
        {
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            if (ev == WM_LBUTTONUP) OpenRequested?.Invoke();
            else if (ev == WM_RBUTTONUP) ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == WM_COMMAND)
        {
            switch ((int)(wParam.ToInt64() & 0xFFFF))
            {
                case IdOpen: OpenRequested?.Invoke(); return IntPtr.Zero;
                case IdNew: NewFaxRequested?.Invoke(); return IntPtr.Zero;
                case IdExit: ExitRequested?.Invoke(); return IntPtr.Zero;
            }
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, IdOpen, "Open PHax");
        AppendMenu(menu, 0, IdNew, "New fax…");
        AppendMenu(menu, 0x800, 0, null);
        AppendMenu(menu, 0, IdExit, "Exit");
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        TrackPopupMenu(menu, 0, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = CreateData("");
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        RemoveWindowSubclass(_hwnd, _proc, 1);
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, nuint id, IntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, int flags, int id, string? text);
    [DllImport("user32.dll")] private static extern bool TrackPopupMenu(IntPtr menu, int flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
