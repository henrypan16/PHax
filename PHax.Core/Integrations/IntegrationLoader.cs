using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace PHax.Core.Integrations;

/// <summary>Finds <c>PHax.Integration.*.dll</c> in a folder and lets each integration register its services.</summary>
public static class IntegrationLoader
{
    public const string FilePattern = "PHax.Integration.*.dll";

    /// <summary>Returns one log line per DLL. A broken integration is skipped; PHax must keep working without it.</summary>
    public static IReadOnlyList<string> Load(IServiceCollection services, string directory)
    {
        var messages = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, FilePattern).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var assembly = Assembly.LoadFrom(file);
                var types = assembly.GetExportedTypes()
                    .Where(t => typeof(IPhaxIntegration).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
                    .ToList();
                if (types.Count == 0) messages.Add($"{Path.GetFileName(file)}: no IPhaxIntegration found");
                foreach (var type in types)
                {
                    // Register into a scratch collection first so a failure halfway leaves no partial registrations.
                    var staged = new ServiceCollection();
                    ((IPhaxIntegration)Activator.CreateInstance(type)!).ConfigureServices(staged);
                    foreach (var d in staged) services.Add(d);
                    messages.Add($"Loaded integration {type.FullName} from {Path.GetFileName(file)} {assembly.GetName().Version}");
                }
            }
            catch (Exception ex)
            {
                messages.Add($"{Path.GetFileName(file)}: could not load: {ex.GetBaseException().Message}");
            }
        }
        return messages;
    }
}
