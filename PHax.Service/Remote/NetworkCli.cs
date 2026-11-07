using PHax.Core.Models;
using PHax.Core.Remote;
using PHax.Core.Services;

namespace PHax.Service.Remote;

/// <summary>
/// Command-line entry points used by the installer, which runs as an administrator before the app has ever started:
/// <code>
/// PHax.Service.exe --configure-network --mode server [--allow-clients] [--port 47811]
/// PHax.Service.exe --configure-network --mode client --server pharmacy-pc
/// PHax.Service.exe --pair --server pharmacy-pc --code ABCDE-FGHJK
/// </code>
/// </summary>
public static class NetworkCli
{
    /// <summary>Returns the process exit code, or null when <paramref name="args"/> is an ordinary service start.</summary>
    public static async Task<int?> TryRunAsync(string[] args)
    {
        bool configure = args.Contains("--configure-network"), pair = args.Contains("--pair");
        if (!configure && !pair) return null;
        var paths = new AppPaths();
        paths.EnsureCreated();
        var store = new NetworkConfigStore(paths);
        try
        {
            if (configure) Configure(store, args);
            else await store.PairAsync(Option(args, "--server") ?? "", Option(args, "--code") ?? "");
            return 0;
        }
        catch (Exception ex) when (ex is FaxServerException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static void Configure(NetworkConfigStore store, string[] args)
    {
        var config = store.Load();
        config.Mode = Option(args, "--mode")?.ToLowerInvariant() switch
        {
            "server" => NetworkMode.Server,
            "client" => NetworkMode.Client,
            _ => throw new ArgumentException("--mode must be server or client."),
        };
        if (config.Mode == NetworkMode.Server)
        {
            config.AllowClients = args.Contains("--allow-clients");
            if (Option(args, "--port") is { } port)
                config.Port = int.TryParse(port, out var p) && p is >= 1024 and <= 65535 ? p : throw new ArgumentException("--port must be between 1024 and 65535.");
        }
        else if (Option(args, "--server") is { Length: > 0 } server)
        {
            if (ServerAddress.ToBaseUri(server) is null) throw new ArgumentException($"\"{server}\" is not a valid server address.");
            if (!string.Equals(server, config.ServerAddress, StringComparison.OrdinalIgnoreCase)) config.ServerAddress = server;
        }
        store.Save(config);
    }

    private static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
