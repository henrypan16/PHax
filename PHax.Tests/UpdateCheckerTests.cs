using System.Net;
using System.Security.Cryptography;
using System.Text;
using PHax.Core.Updates;
using Xunit;

namespace PHax.Tests;

[Trait("Category", "Unit")]
public sealed class UpdateCheckerTests : IDisposable
{
    private static readonly Version Installed = new(0, 4, 2, 0); // assembly versions have four parts
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phax-tests", "update-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static GitHubAsset Asset(string name, byte[]? content = null) => new()
    {
        Name = name,
        BrowserDownloadUrl = $"https://github.com/henrypan16/PHax/releases/download/v0.5.0/{name}",
        Size = content?.Length ?? 100,
        Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(content ?? new byte[100])).ToLowerInvariant(),
    };

    private static GitHubRelease Release(string tag, params GitHubAsset[] assets) =>
        new() { TagName = tag, Name = $"PHax {tag}", HtmlUrl = $"https://github.com/henrypan16/PHax/releases/tag/{tag}", Assets = [.. assets] };

    [Fact]
    public void NewerRelease_PicksStandardInstaller()
    {
        var r = UpdateChecker.Evaluate(Release("v0.5.0", Asset("PHax-Fillware-Addon-1.0.0.exe"), Asset("PHax-Setup-0.5.0.exe")), Installed);
        Assert.NotNull(r.Update);
        Assert.Equal("PHax-Setup-0.5.0.exe", r.Update.AssetName);
        Assert.Equal(new Version(0, 5, 0), r.Update.Version);
        Assert.Equal(64, r.Update.Sha256.Length);
    }

    [Fact]
    public void ReleaseWithoutTheInstaller_IsNotInstallable()
    {
        var r = UpdateChecker.Evaluate(Release("v0.5.0", Asset("PHax-Fillware-Addon-1.0.0.exe")), Installed);
        Assert.Null(r.Update);
        Assert.Contains("PHax-Setup-0.5.0.exe", r.Message);
    }

    [Theory]
    [InlineData("v0.4.2")]
    [InlineData("v0.3.0")]
    public void SameOrOlderRelease_IsUpToDate(string tag)
    {
        var r = UpdateChecker.Evaluate(Release(tag, Asset($"PHax-Setup-{tag[1..]}.exe")), Installed);
        Assert.Null(r.Update);
        Assert.Contains("up to date", r.Message);
    }

    [Fact]
    public void MissingChecksum_IsNotInstallable()
    {
        var asset = Asset("PHax-Setup-0.5.0.exe");
        asset.Digest = null;
        Assert.Null(UpdateChecker.Evaluate(Release("v0.5.0", asset), Installed).Update);
    }

    [Fact]
    public void PrereleaseAndBadTags_AreIgnored()
    {
        var pre = Release("v0.5.0", Asset("PHax-Setup-0.5.0.exe"));
        pre.Prerelease = true;
        Assert.Null(UpdateChecker.Evaluate(pre, Installed).Update);
        Assert.Null(UpdateChecker.Evaluate(Release("latest", Asset("PHax-Setup-0.5.0.exe")), Installed).Update);
    }

    [Fact]
    public async Task Check_ParsesGitHubJson()
    {
        const string json = """
            {"tag_name":"v0.5.0","name":"PHax v0.5.0","html_url":"https://github.com/henrypan16/PHax/releases/tag/v0.5.0","draft":false,"prerelease":false,
             "assets":[{"name":"PHax-Setup-0.5.0.exe","size":106056750,"digest":"sha256:7d0c8352a59fc3d337a428b517c26700c3a88a521182701e27d4c86bf055daa0",
                        "browser_download_url":"https://github.com/henrypan16/PHax/releases/download/v0.5.0/PHax-Setup-0.5.0.exe"}]}
            """;
        using var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8) }));
        var r = await new UpdateChecker(http).CheckAsync(Installed, default);
        Assert.Equal("PHax v0.5.0", r.Update!.ReleaseName);
        Assert.Equal(106056750, r.Update.Size);
        Assert.Equal("7D0C8352A59FC3D337A428B517C26700C3A88A521182701E27D4C86BF055DAA0", r.Update.Sha256);
    }

    [Fact]
    public async Task Download_VerifiesChecksum()
    {
        var content = RandomNumberGenerator.GetBytes(300_000);
        var update = UpdateChecker.Evaluate(Release("v0.5.0", Asset("PHax-Setup-0.5.0.exe", content)), Installed).Update!;

        using var good = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }));
        var path = await new UpdateChecker(good).DownloadAsync(update, _dir, null, default);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));

        File.Delete(path);
        var tampered = (byte[])content.Clone();
        tampered[1234] ^= 0xFF;
        using var bad = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tampered) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateChecker(bad).DownloadAsync(update, _dir, null, default));
        Assert.Empty(Directory.GetFiles(_dir)); // neither the installer nor the partial download is left behind
    }

    [Fact]
    public async Task Download_ReportsProgressAtMostOncePerPercent()
    {
        var content = RandomNumberGenerator.GetBytes(5_000_000); // ~60 read chunks per percent would flood the UI thread
        var update = UpdateChecker.Evaluate(Release("v0.5.0", Asset("PHax-Setup-0.5.0.exe", content)), Installed).Update!;
        using var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }));
        var reports = new List<double>();
        await new UpdateChecker(http).DownloadAsync(update, _dir, new SyncProgress(reports.Add), default);
        Assert.InRange(reports.Count, 2, 101);
        Assert.Equal(reports.Distinct().Count(), reports.Count);
        Assert.Equal(1.0, reports[^1]);
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
