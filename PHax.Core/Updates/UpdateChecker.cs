using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PHax.Core.Updates;

/// <summary>An installer from a GitHub release that is newer than the running build.</summary>
public sealed record UpdateInfo(Version Version, string ReleaseName, string ReleaseUrl, string AssetName, Uri DownloadUrl, long Size, string Sha256);

/// <summary>What a check found. <see cref="Update"/> is set only when an installable newer version exists.</summary>
public sealed record UpdateCheckResult(UpdateInfo? Update, string Message);

/// <summary>
/// Finds and downloads the PHax installer (<c>PHax-Setup-x.y.z.exe</c>) from the latest GitHub release. Add-ons have
/// their own installers and folder, so updating PHax leaves them in place. Downloads must match the SHA-256 digest
/// GitHub reports for the asset.
/// </summary>
public sealed class UpdateChecker(HttpClient http, string repository = UpdateChecker.DefaultRepository)
{
    public const string DefaultRepository = "henrypan16/PHax";

    public async Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new(null, "No PHax release has been published yet.");
        if ((int)response.StatusCode is 403 or 429) return new(null, "GitHub is limiting requests from this network. Try again in an hour.");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Evaluate(JsonSerializer.Deserialize<GitHubRelease>(json), current);
    }

    /// <summary>Picks the installer from a release; pure, so it can be tested without the network.</summary>
    public static UpdateCheckResult Evaluate(GitHubRelease? release, Version current)
    {
        if (release is null || release.Draft || release.Prerelease) return new(null, "No PHax release is available.");
        if (!TryParseVersion(release.TagName, out var latest)) return new(null, $"The latest release ({release.TagName}) has no recognisable version number.");
        current = Normalize(current);
        if (latest <= current) return new(null, $"PHax {Format(current)} is up to date.");

        var assetName = InstallerName(latest);
        var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
            return new(null, $"PHax {Format(latest)} is available, but its release has no {assetName} to install.");
        if (!TryParseDigest(asset.Digest, out var sha256) || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            return new(null, $"PHax {Format(latest)} is available, but its installer has no checksum on GitHub, so it cannot be installed automatically.");
        var name = string.IsNullOrWhiteSpace(release.Name) ? $"PHax {Format(latest)}" : release.Name;
        return new(new UpdateInfo(latest, name, release.HtmlUrl, asset.Name, url, asset.Size, sha256), $"{name} is available.");
    }

    /// <summary>Downloads the installer into <paramref name="directory"/> and verifies size and SHA-256. Returns its path.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, string directory, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetFileName(update.AssetName));
        var partial = path + ".part";
        try
        {
            using (var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                int n, lastPercent = -1;
                while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > update.Size) throw new InvalidDataException("The download is larger than the installer GitHub lists.");
                    hash.AppendData(buffer, 0, n);
                    await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    // Once per percent: each report is posted to the UI thread, which must stay free.
                    int percent = update.Size > 0 ? (int)(total * 100 / update.Size) : 0;
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(percent / 100.0);
                    }
                }
                if (total != update.Size) throw new InvalidDataException("The download is incomplete.");
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded installer does not match its published checksum.");
            }
            File.Move(partial, path, overwrite: true);
            return path;
        }
        finally
        {
            try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static string InstallerName(Version version) => $"PHax-Setup-{Format(version)}.exe";

    public static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

    public static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var text = tag?.Trim().TrimStart('v', 'V');
        if (string.IsNullOrEmpty(text) || !Version.TryParse(text, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    /// <summary>Three parts, so 0.4.2.0 (assembly version) and 0.4.2 (tag) compare equal.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static bool TryParseDigest(string? digest, out string sha256)
    {
        sha256 = "";
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var hex = digest[prefix.Length..];
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) return false;
        sha256 = hex.ToUpperInvariant();
        return true;
    }
}

/// <summary>The fields of GitHub's release JSON that the updater uses.</summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    /// <summary>"sha256:&lt;hex&gt;", computed by GitHub when the asset was uploaded.</summary>
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}
