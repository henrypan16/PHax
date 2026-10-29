using System.IO.Ports;
using Microsoft.Win32;

namespace PHax.Modem.Serial;

public sealed record SerialPortInfo(string PortName, string? FriendlyName, string? DeviceId, bool IsUsb)
{
    public override string ToString() => FriendlyName is { Length: > 0 } ? $"{PortName} — {FriendlyName}" : PortName;
}

public static class SerialPortDiscovery
{
    /// <summary>Lists COM ports with friendly names from the Plug and Play registry where available.</summary>
    public static IReadOnlyList<SerialPortInfo> List()
    {
        var names = SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var friendly = new Dictionary<string, (string Name, string Id)>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            try { ScanEnum(friendly); } catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return names
            .OrderBy(n => int.TryParse(n.AsSpan(3), out var i) ? i : int.MaxValue)
            .Select(n => friendly.TryGetValue(n, out var f)
                ? new SerialPortInfo(n, f.Name, f.Id, f.Id.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
                : new SerialPortInfo(n, null, null, false))
            .ToList();
    }

    private static void ScanEnum(Dictionary<string, (string, string)> result)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
        if (root is null) return;
        foreach (var bus in new[] { "USB", "ACPI", "PCI", "USBSER", "MDMCPQ", "ROOT" })
        {
            using var busKey = root.OpenSubKey(bus);
            if (busKey is null) continue;
            foreach (var deviceName in busKey.GetSubKeyNames())
            {
                using var device = busKey.OpenSubKey(deviceName);
                if (device is null) continue;
                foreach (var instanceName in device.GetSubKeyNames())
                {
                    using var instance = device.OpenSubKey(instanceName);
                    using var parameters = instance?.OpenSubKey("Device Parameters");
                    if (parameters?.GetValue("PortName") is string port && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = instance!.GetValue("FriendlyName") as string ?? instance.GetValue("DeviceDesc") as string ?? "";
                        // DeviceDesc may be "@oem.inf,%id%;Readable name".
                        var semi = name.LastIndexOf(';');
                        if (semi >= 0) name = name[(semi + 1)..];
                        result[port] = (name, $@"{bus}\{deviceName}\{instanceName}");
                    }
                }
            }
        }
    }
}
