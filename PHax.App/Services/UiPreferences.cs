using System.Text.Json;

namespace PHax.App.Services;

/// <summary>
/// Layout choices of the signed-in Windows user (<c>%LOCALAPPDATA%\PHax\ui.json</c>). Kept out of
/// <see cref="PHax.Core.Models.AppSettings"/> because those are shared by everyone on the PC (and the service), and a
/// Settings page open with an older copy would otherwise overwrite them.
/// </summary>
public static class UiPreferences
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PHax", "ui.json");

    private static Data? _data;

    /// <summary>Raised on the UI thread after <see cref="ShowPreviewPane"/> changes.</summary>
    public static event EventHandler? Changed;

    /// <summary>The preview pane to the right of the fax lists.</summary>
    public static bool ShowPreviewPane
    {
        get => Current.ShowPreviewPane;
        set
        {
            if (Current.ShowPreviewPane == value) return;
            Current.ShowPreviewPane = value;
            Save();
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    private static Data Current => _data ??= Load();

    private static Data Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Serilog.Log.Warning("Could not read {Path}: {Message}", FilePath, ex.Message);
            return new();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a layout choice: it still applies until PHax closes.
            Serilog.Log.Warning("Could not save {Path}: {Message}", FilePath, ex.Message);
        }
    }

    private sealed class Data
    {
        public bool ShowPreviewPane { get; set; } = true;
    }
}
