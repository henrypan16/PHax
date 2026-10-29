using System.Diagnostics;
using System.ServiceProcess;

namespace PHax.Service;

/// <summary>
/// Explains "port in use" errors by naming likely owners. Windows has no simple API to ask which process holds a
/// COM port, so this checks the usual suspects: the Windows Fax service, other fax programs, and a second PHax
/// service process.
/// </summary>
public static class PortConflict
{
    public static bool IsInUse(Exception ex) =>
        ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase) || ex.InnerException is UnauthorizedAccessException;

    public static string Describe(string portName)
    {
        var hints = new List<string>();
        try
        {
            using var fax = new ServiceController("Fax");
            if (fax.Status == ServiceControllerStatus.Running)
                hints.Add("the Windows Fax service (\"Fax\") is running. Stop it and set it to Disabled in services.msc");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* not installed */ }

        int me = Environment.ProcessId;
        var suspects = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == me) continue;
                var name = p.ProcessName;
                if (name.Equals("PHax", StringComparison.OrdinalIgnoreCase)) continue; // the desktop app never opens the port
                if (name.Equals("PHax.Service", StringComparison.OrdinalIgnoreCase)) suspects.Add($"another PHax.Service (PID {p.Id})");
                else if (name.Contains("fax", StringComparison.OrdinalIgnoreCase) || name.Equals("WFS", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("fillware", StringComparison.OrdinalIgnoreCase) || name.Contains("putty", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("hypertrm", StringComparison.OrdinalIgnoreCase) || name.Contains("teraterm", StringComparison.OrdinalIgnoreCase))
                    suspects.Add($"{name}.exe (PID {p.Id})");
            }
        }
        if (suspects.Count > 0) hints.Add("these programs may be holding it: " + string.Join(", ", suspects.Distinct()));

        return hints.Count == 0
            ? $"Close any other fax, modem or terminal program that uses {portName}, or restart the computer."
            : $"Possible cause: {string.Join("; ", hints)}.";
    }
}
