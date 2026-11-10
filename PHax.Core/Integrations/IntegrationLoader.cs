using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace PHax.Core.Integrations;

/// <summary>
/// Version of the integration contract (<see cref="IPhaxIntegration"/>, <see cref="IAppIntegration"/> and the services
/// they get). Bump it when a change would break add-ons built against the previous PHax, so those are skipped with a
/// clear message instead of failing at run time.
/// </summary>
public static class IntegrationApi
{
    public const int Version = 1;
}

/// <summary>Declares the <see cref="IntegrationApi.Version"/> an add-on was built for. Add-ons without it are version 1.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class PhaxIntegrationApiAttribute(int version) : Attribute
{
    public int Version { get; } = version;
}

/// <summary>What <see cref="IntegrationLoader.Load"/> did: one log line per DLL, and the failures to show the user.</summary>
public sealed record IntegrationLoadResult(IReadOnlyList<string> Messages, IReadOnlyList<string> Problems);

/// <summary>
/// Finds <c>PHax.Integration.*.dll</c> and lets each integration register its services. Add-ons are installed by their
/// own installers into <c>Addons\&lt;Name&gt;\</c> next to the app folder, so updating PHax leaves them in place; a DLL
/// directly in the app folder (development builds) is loaded too. Assemblies the app ships always win over an add-on's
/// copy, because the app's own are resolved first; the add-on's extra dependencies are found next to its DLL.
/// </summary>
public static class IntegrationLoader
{
    public const string FilePattern = "PHax.Integration.*.dll";
    public const string AddonsFolderName = "Addons";

    /// <summary>The add-ons folder for an app installed in <c>{app}\App</c>: <c>{app}\Addons</c>.</summary>
    public static string AddonsDirectoryFor(string appDirectory) =>
        Path.GetFullPath(Path.Combine(appDirectory, "..", AddonsFolderName));

    /// <summary>A broken or incompatible integration is skipped; PHax must keep working without it.</summary>
    public static IntegrationLoadResult Load(IServiceCollection services, string appDirectory, string? addonsDirectory = null)
    {
        var messages = new List<string>();
        var problems = new List<string>();
        foreach (var file in FindDlls(appDirectory, addonsDirectory))
        {
            var name = Path.GetFileName(file);
            try
            {
                var assembly = Assembly.LoadFrom(file);
                if (ApiProblem(assembly.GetCustomAttribute<PhaxIntegrationApiAttribute>()?.Version ?? 1) is { } apiProblem)
                {
                    Add(problems, messages, $"{AddonName(name)}: {apiProblem}");
                    continue;
                }
                var types = assembly.GetExportedTypes()
                    .Where(t => typeof(IPhaxIntegration).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
                    .ToList();
                if (types.Count == 0) messages.Add($"{name}: no IPhaxIntegration found");
                foreach (var type in types)
                {
                    // Register into a scratch collection first so a failure halfway leaves no partial registrations.
                    var staged = new ServiceCollection();
                    ((IPhaxIntegration)Activator.CreateInstance(type)!).ConfigureServices(staged);
                    foreach (var d in staged) services.Add(d);
                    messages.Add($"Loaded integration {type.FullName} from {file} {assembly.GetName().Version}");
                }
            }
            catch (Exception ex)
            {
                Add(problems, messages, $"{AddonName(name)}: could not load: {ex.GetBaseException().Message}");
            }
        }
        return new IntegrationLoadResult(messages, problems);
    }

    /// <summary>Null when an add-on built for <paramref name="declared"/> can run in this PHax.</summary>
    public static string? ApiProblem(int declared) => declared == IntegrationApi.Version ? null
        : declared < IntegrationApi.Version
            ? $"the add-on was built for an older PHax (integration API {declared}, this PHax uses {IntegrationApi.Version}). Install an updated add-on."
            : $"the add-on needs a newer PHax (integration API {declared}, this PHax uses {IntegrationApi.Version}). Update PHax.";

    /// <summary>Add-on folders first; the same file name in the app folder is then ignored.</summary>
    private static IEnumerable<string> FindDlls(string appDirectory, string? addonsDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> addonDlls = addonsDirectory is not null && Directory.Exists(addonsDirectory)
            ? Directory.EnumerateDirectories(addonsDirectory).Order(StringComparer.OrdinalIgnoreCase)
                .SelectMany(d => Directory.EnumerateFiles(d, FilePattern).Order(StringComparer.OrdinalIgnoreCase))
            : [];
        IEnumerable<string> appDlls = Directory.Exists(appDirectory)
            ? Directory.EnumerateFiles(appDirectory, FilePattern).Order(StringComparer.OrdinalIgnoreCase)
            : [];
        foreach (var file in addonDlls.Concat(appDlls))
            if (seen.Add(Path.GetFileName(file))) yield return file;
    }

    private static string AddonName(string fileName) =>
        fileName.StartsWith("PHax.Integration.", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? $"{fileName["PHax.Integration.".Length..^".dll".Length]} add-on"
            : fileName;

    private static void Add(List<string> problems, List<string> messages, string problem)
    {
        problems.Add(problem);
        messages.Add(problem);
    }
}
