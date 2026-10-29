using System.Runtime.InteropServices;

namespace PHax.App.Services;

/// <summary>
/// Allows one PHax window per Windows session. A second launch signals the running instance to come to the
/// front and exits.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\PHax.App.Instance";
    private const string ShowEventName = @"Local\PHax.App.Show";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    /// <summary>Returns true if this process is the first instance.</summary>
    public static bool TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
        if (!created)
        {
            mutex.Dispose();
            return false;
        }
        _mutex = mutex;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        return true;
    }

    /// <summary>Asks the running instance to show its window (and lets it take the foreground).</summary>
    public static void ActivateExisting()
    {
        AllowSetForegroundWindow(-1);
        for (int i = 0; i < 20; i++)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
            {
                using (ev) ev.Set();
                return;
            }
            Thread.Sleep(100);
        }
    }

    /// <summary>Calls <paramref name="onShow"/> whenever another launch is attempted.</summary>
    public static void Listen(Action onShow)
    {
        var ev = _showEvent ?? throw new InvalidOperationException("Call TryAcquire first.");
        var thread = new Thread(() =>
        {
            while (ev.WaitOne()) onShow();
        }) { IsBackground = true, Name = "PHax single-instance listener" };
        thread.Start();
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
