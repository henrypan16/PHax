using System.Text.Json;
using PHax.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace PHax.Data;

/// <summary>Loads and saves <see cref="AppSettings"/> as one JSON document.</summary>
public sealed class SettingsStore(IDbContextFactory<FaxDbContext> factory)
{
    private const string Key = "app";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var record = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == Key, ct);
        if (record is null) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(record.Value, Json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(settings, Json);
        var record = await db.Settings.FirstOrDefaultAsync(s => s.Key == Key, ct);
        if (record is null) db.Settings.Add(new AppSettingRecord { Key = Key, Value = json });
        else
        {
            record.Value = json;
            record.UpdatedUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
