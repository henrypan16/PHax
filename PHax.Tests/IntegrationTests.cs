using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PHax.Core.Integrations;
using PHax.Core.Models;
using Xunit;

namespace PHax.Tests;

[Trait("Category", "Unit")]
public sealed class IntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phax-tests", Guid.NewGuid().ToString("N"));

    public IntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Loader_EmptyFolder_LoadsNothing()
    {
        var services = new ServiceCollection();
        Assert.Empty(IntegrationLoader.Load(services, _dir));
        Assert.Empty(services);
    }

    [Fact]
    public void Loader_BrokenDll_IsSkippedWithMessage()
    {
        File.WriteAllText(Path.Combine(_dir, "PHax.Integration.Broken.dll"), "not an assembly");
        var services = new ServiceCollection();
        Assert.Contains("could not load", Assert.Single(IntegrationLoader.Load(services, _dir)));
        Assert.Empty(services);
    }

    [Fact]
    public void IntegrationSettings_SurviveSaveByBuildWithoutTheIntegration()
    {
        // Shape stored by earlier builds that had a typed IntegrationSettings class.
        const string stored = """{ "General": { "StartWithWindows": true }, "Integration": { "WatchSomething": false, "Other": "x" } }""";
        var settings = JsonSerializer.Deserialize<AppSettings>(stored)!;
        Assert.False(settings.Integration.GetBool("WatchSomething", true));
        Assert.True(settings.Integration.GetBool("Missing", true));
        Assert.True(settings.Integration.GetBool("Other", true)); // wrong type → fallback

        settings.Integration.SetBool("New", false);
        var again = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.False(again.Integration.GetBool("WatchSomething", true));
        Assert.False(again.Integration.GetBool("New", true));
        Assert.Equal("x", again.Integration.Values["Other"].GetString());
    }
}
