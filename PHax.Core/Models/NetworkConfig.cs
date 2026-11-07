using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PHax.Core.Services;

namespace PHax.Core.Models;

/// <summary>How this PC takes part in fax traffic.</summary>
public enum NetworkMode
{
    /// <summary>This PC owns the modem. Other PCs may connect when <see cref="NetworkConfig.AllowClients"/> is on.</summary>
    Server = 0,
    /// <summary>This PC uses the modem of another PC running PHax in server mode; the local service leaves the COM port alone.</summary>
    Client = 1,
}

/// <summary>
/// Per-machine network role. Kept in a file rather than in <see cref="AppSettings"/> because the installer writes it
/// before any database exists, and because it decides which modem and fax store the app talks to in the first place.
/// </summary>
public sealed class NetworkConfig
{
    public const int DefaultPort = 47811;

    public NetworkMode Mode { get; set; } = NetworkMode.Server;

    // Server mode.
    /// <summary>Off by default: nothing listens on the network until someone turns this on.</summary>
    public bool AllowClients { get; set; }
    public int Port { get; set; } = DefaultPort;

    // Client mode.
    /// <summary>Host name or IP of the server, optionally with ":port".</summary>
    public string ServerAddress { get; set; } = "";
    /// <summary>SHA-256 thumbprint of the server's certificate, pinned during pairing.</summary>
    public string ServerThumbprint { get; set; } = "";
    /// <summary>Access token protected with machine-scope DPAPI: every user of this PC shares the pairing.</summary>
    public string ProtectedToken { get; set; } = "";

    [JsonIgnore] public bool IsPaired => ServerThumbprint.Length > 0 && ProtectedToken.Length > 0;
}

/// <summary>Reads and writes <see cref="NetworkConfig"/>. The service, the app and the installer all use this file.</summary>
public sealed class NetworkConfigStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly byte[] Entropy = "PHax.NetworkToken.v1"u8.ToArray();

    public bool Exists => File.Exists(paths.NetworkConfigPath);

    /// <summary>Missing or unreadable files mean the default: server mode with the network off.</summary>
    public NetworkConfig Load()
    {
        try { return JsonSerializer.Deserialize<NetworkConfig>(File.ReadAllText(paths.NetworkConfigPath), Json) ?? new NetworkConfig(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new NetworkConfig(); }
    }

    public void Save(NetworkConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.NetworkConfigPath)!);
        var tmp = paths.NetworkConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Json));
        File.Move(tmp, paths.NetworkConfigPath, overwrite: true);
    }

    /// <summary>Pairs this PC with a server and records the result, which also makes it a client.</summary>
    public async Task PairAsync(string server, string code, CancellationToken ct = default)
    {
        var result = await Remote.FaxServerClient.PairAsync(server, code, Environment.MachineName, ct);
        var config = Load();
        config.Mode = NetworkMode.Client;
        config.ServerAddress = server.Trim();
        config.ServerThumbprint = result.Thumbprint;
        config.ProtectedToken = ProtectToken(result.Token);
        Save(config);
    }

    public static string ProtectToken(string token) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.LocalMachine));

    /// <summary>Null when the token cannot be read, e.g. a config copied from another PC.</summary>
    public static string? UnprotectToken(string protectedToken)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), Entropy, DataProtectionScope.LocalMachine)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }
}
