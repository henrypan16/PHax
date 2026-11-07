using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using PHax.Core.Models;
using PHax.Core.Remote;
using PHax.Core.Services;
using PHax.Data;
using Microsoft.EntityFrameworkCore;

namespace PHax.Service.Remote;

/// <summary>What the network listener is doing, shared between <see cref="NetworkServerHost"/> and the commands that change it.</summary>
public sealed class NetworkServerState
{
    private readonly SemaphoreSlim _reload = new(0, 1);

    public bool Listening { get; set; }
    public int Port { get; set; }
    public string? Error { get; set; }

    /// <summary>Asks the host to apply the current <see cref="NetworkConfig"/>.</summary>
    public void RequestReload()
    {
        try { _reload.Release(); } catch (SemaphoreFullException) { /* one is already pending */ }
    }

    public Task WaitForReloadAsync(CancellationToken ct) => _reload.WaitAsync(ct);
}

/// <summary>The self-signed TLS certificate clients pin during pairing.</summary>
public sealed class ServerCertificate(AppPaths paths, ILogger<ServerCertificate> log)
{
    private const string FileName = "server.pfx";

    public X509Certificate2 LoadOrCreate()
    {
        var dir = paths.ServerSecretsDirectory;
        Directory.CreateDirectory(dir);
        Restrict(dir);
        var file = Path.Combine(dir, FileName);
        if (File.Exists(file))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(file, null);
                if (existing.HasPrivateKey) return existing;
                existing.Dispose();
            }
            catch (CryptographicException ex) { log.LogWarning(ex, "The server certificate could not be read; creating a new one. Paired PCs must connect again."); }
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=PHax Server {Environment.MachineName}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Environment.MachineName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        // Clients pin this exact certificate, so a long life avoids an expiry that would disconnect every PC at once.
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(25));
        var pfx = created.Export(X509ContentType.Pfx);
        File.WriteAllBytes(file, pfx);
        log.LogInformation("Created the server certificate");
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }

    /// <summary>The data root is writable by all users so the app can share the database; the private key must not be.</summary>
    private static void Restrict(string dir)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        void Allow(IdentityReference who) =>
            security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        Allow(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        // Running from a console during development: the owner still needs the folder.
        Allow(WindowsIdentity.GetCurrent().User!);
        new DirectoryInfo(dir).SetAccessControl(security);
    }
}

/// <summary>Issues one-time pairing codes and turns a valid code into a client token.</summary>
public sealed class PairingService(IDbContextFactory<FaxDbContext> dbFactory)
{
    /// <summary>No 0/O or 1/I: the code is read off one screen and typed on another.</summary>
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private sealed class ActiveCode(string code, DateTime expiresUtc)
    {
        public string Code { get; } = code;
        public DateTime ExpiresUtc { get; } = expiresUtc;
        public int Attempts { get; set; }
    }

    private readonly object _lock = new();
    private ActiveCode? _active;

    /// <summary>Raised when a PC is removed, so its open event streams can be closed at once.</summary>
    public event Action<Guid>? ClientRemoved;

    public (string Code, DateTime ExpiresUtc) CreateCode()
    {
        var chars = RandomNumberGenerator.GetItems<char>(Alphabet, 10);
        var code = $"{new string(chars, 0, 5)}-{new string(chars, 5, 5)}";
        var expires = DateTime.UtcNow + Lifetime;
        lock (_lock) _active = new ActiveCode(code, expires);
        return (code, expires);
    }

    /// <summary>Returns null when the proof is wrong or no code is active. A code works once and dies after a few wrong tries.</summary>
    public async Task<PairResponse?> TryPairAsync(string clientName, string proof, string thumbprint, CancellationToken ct)
    {
        ActiveCode? active;
        lock (_lock)
        {
            active = _active;
            if (active is null) return null;
            if (DateTime.UtcNow > active.ExpiresUtc || ++active.Attempts > MaxAttempts) { _active = null; return null; }
        }
        // The key derivation is slow on purpose; it runs outside the lock and only while a code is active.
        var expected = await Task.Run(() => PairingProof.Client(active.Code, thumbprint), ct);
        if (!PairingProof.Matches(expected, proof)) return null;
        lock (_lock)
        {
            if (!ReferenceEquals(_active, active)) return null; // used by a concurrent request
            _active = null;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // The same PC pairing again (e.g. after a reinstall) keeps its identity, and with it its faxes.
        var client = await db.PairedClients.FirstOrDefaultAsync(c => c.Name == clientName, ct);
        if (client is null) db.PairedClients.Add(client = new PairedClient { Name = clientName });
        client.TokenHash = HashToken(token);
        await db.SaveChangesAsync(ct);
        return new PairResponse(client.Id, token, await Task.Run(() => PairingProof.Server(active.Code, thumbprint), ct));
    }

    public async Task<List<PairedClientDto>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var clients = await db.PairedClients.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        return clients.ConvertAll(c => new PairedClientDto(c.Id, c.Name, c.CreatedUtc, c.LastSeenUtc, c.LastAddress));
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.PairedClients.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        ClientRemoved?.Invoke(id);
    }

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}

/// <summary>Keeps Windows Firewall in step with the listener. Scoped to the local subnet on private and domain networks.</summary>
public static class FirewallRule
{
    private const string RuleName = "PHax Server";

    public static bool Allow(int port, ILogger log)
    {
        Run($"advfirewall firewall delete rule name=\"{RuleName}\"", log);
        return Run($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} " +
                   $"profile=private,domain remoteip=localsubnet program=\"{Environment.ProcessPath}\"", log);
    }

    public static void Remove(ILogger log) => Run($"advfirewall firewall delete rule name=\"{RuleName}\"", log);

    private static bool Run(string arguments, ILogger log)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh.exe", arguments) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            if (process is null) return false;
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000)) { process.Kill(); return false; }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.LogWarning(ex, "Could not update the firewall rule");
            return false;
        }
    }
}

public static class LocalNetwork
{
    /// <summary>A last check behind the firewall rule: this API is for the local network, whatever the firewall says.</summary>
    public static bool IsLocal(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>What a client could type to reach this PC: the computer name first, then its IPv4 addresses.</summary>
    public static List<string> Addresses()
    {
        var result = new List<string> { Environment.MachineName };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            result.AddRange(nic.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && IsLocal(a.Address))
                .Select(a => a.Address.ToString()));
        }
        return result;
    }
}
