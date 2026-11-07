using System.Security.Cryptography;
using System.Text;
using PHax.Core.Models;

namespace PHax.Core.Remote;

/// <summary>Which outbound faxes a client asks for. Drafts stay on the client and never reach the server.</summary>
public enum OutboundView { Outbox, Sent }

public static class RemoteDocument
{
    /// <summary>Stands in for a server file path in faxes sent to clients: the document exists, fetch it by fax id.</summary>
    public const string Marker = "server";
}

public sealed record PairRequest(string ClientName, string Proof);
public sealed record PairResponse(Guid ClientId, string Token, string ServerProof);
public sealed record RemoteConfigDto(string ServerName, string CoverPageFrom, string StationId, bool DefaultCoverPage);
public sealed record SubmitFaxRequest(string RecipientName, string FaxNumber, string Subject, bool IncludeCoverPage, string CoverMessage);
public sealed record RenameRequest(string Name);
public sealed record ReadRequest(bool IsRead);
public sealed record CountDto(int Count);
public sealed record MessageDto(string Message);
public sealed record ErrorDto(string Error);

/// <summary>A PC allowed to connect, as listed on the server's Settings page.</summary>
public sealed record PairedClientDto(Guid Id, string Name, DateTime CreatedUtc, DateTime? LastSeenUtc, string? LastAddress);

/// <summary>A one-time code shown on the server, with the addresses a client can use to reach it.</summary>
public sealed record PairingCodeDto(string Code, DateTime ExpiresUtc, int Port, IReadOnlyList<string> Addresses);

/// <summary>The certificate-bound proofs exchanged during pairing.</summary>
/// <remarks>
/// A client has nothing to check a self-signed certificate against the first time it connects, so the one-time code
/// does that job: both proofs are keyed by the code and bound to the certificate thumbprint each side sees. A
/// machine in the middle presents a different certificate, so its proof fails on the server, and it cannot forge the
/// server's proof without the code. The key derivation slows down guessing the code from a captured proof.
/// </remarks>
public static class PairingProof
{
    private const int Iterations = 200_000;

    /// <summary>Upper-case without separators, so a code typed in any style matches.</summary>
    public static string NormalizeCode(string code) =>
        new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    public static string Client(string code, string thumbprint) => Compute(code, thumbprint, "client");
    public static string Server(string code, string thumbprint) => Compute(code, thumbprint, "server");

    public static bool Matches(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    private static string Compute(string code, string thumbprint, string role)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(NormalizeCode(code)),
            Encoding.ASCII.GetBytes(thumbprint.ToUpperInvariant()), Iterations, HashAlgorithmName.SHA256, 32);
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(role)));
    }
}

/// <summary>Parses what people type into "server address".</summary>
public static class ServerAddress
{
    /// <summary>Accepts "host", "host:port", "192.168.1.5:47811" and "[fe80::1]:47811". Null when it is not a plain address.</summary>
    public static Uri? ToBaseUri(string? address)
    {
        var text = address?.Trim();
        if (string.IsNullOrEmpty(text) || text.IndexOfAny(['/', '@', '?', '#', ' ']) >= 0) return null;
        if (!Uri.TryCreate($"https://{text}", UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
        bool explicitPort = !uri.IsDefaultPort || text.EndsWith(":443", StringComparison.Ordinal);
        return new UriBuilder("https", uri.Host, explicitPort ? uri.Port : NetworkConfig.DefaultPort).Uri;
    }
}
