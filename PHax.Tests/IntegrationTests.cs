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
        var result = IntegrationLoader.Load(services, _dir, Path.Combine(_dir, "Addons"));
        Assert.Empty(result.Messages);
        Assert.Empty(result.Problems);
        Assert.Empty(services);
    }

    [Fact]
    public void Loader_BrokenDll_IsSkippedWithMessage()
    {
        File.WriteAllText(Path.Combine(_dir, "PHax.Integration.Broken.dll"), "not an assembly");
        var services = new ServiceCollection();
        var result = IntegrationLoader.Load(services, _dir);
        Assert.Contains("could not load", Assert.Single(result.Messages));
        Assert.StartsWith("Broken add-on:", Assert.Single(result.Problems));
        Assert.Empty(services);
    }

    [Fact]
    public void Loader_FindsAddonsInTheirOwnFolders_AndPrefersThemOverTheAppFolder()
    {
        var app = Path.Combine(_dir, "App");
        var addons = IntegrationLoader.AddonsDirectoryFor(app);
        Assert.Equal(Path.Combine(_dir, "Addons"), addons);
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(Path.Combine(addons, "One"));
        Directory.CreateDirectory(Path.Combine(addons, "Two"));
        File.WriteAllText(Path.Combine(addons, "One", "PHax.Integration.One.dll"), "x");
        File.WriteAllText(Path.Combine(addons, "Two", "PHax.Integration.Two.dll"), "x");
        File.WriteAllText(Path.Combine(app, "PHax.Integration.Two.dll"), "x"); // also in the app folder (dev build)

        var result = IntegrationLoader.Load(new ServiceCollection(), app, addons);
        Assert.Equal(["One add-on", "Two add-on"], result.Problems.Select(p => p[..p.IndexOf(':')]));
        Assert.Contains(result.Messages, m => m.Contains("Two")); // loaded once, from Addons
        Assert.Equal(2, result.Messages.Count);
    }

    [Fact]
    public void ApiVersion_MismatchIsExplained()
    {
        Assert.Null(IntegrationLoader.ApiProblem(IntegrationApi.Version));
        Assert.Contains("older PHax", IntegrationLoader.ApiProblem(IntegrationApi.Version - 1));
        Assert.Contains("newer PHax", IntegrationLoader.ApiProblem(IntegrationApi.Version + 1));
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
