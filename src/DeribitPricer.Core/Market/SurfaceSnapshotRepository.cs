using System.Text.Json;
using System.Text.RegularExpressions;
using DeribitPricer.Core.Pricing;

namespace DeribitPricer.Core.Market;

public sealed record SurfaceSnapshot(string Id, DateTime SavedAtUtc, VolatilitySurface Surface);
public sealed record SurfaceSnapshotInfo(string Id, string Currency, DateTime AsOfUtc, DateTime SavedAtUtc, string Mode);

// Un fichier par observation, écriture atomique, identifiants opaques (aucun chemin fourni par l'appelant).
public sealed class SurfaceSnapshotRepository(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex ValidId = new("^[a-f0-9]{32}$", RegexOptions.CultureInvariant);

    public async Task<SurfaceSnapshot> SaveAsync(VolatilitySurface surface, CancellationToken ct = default)
    {
        if (surface.Slices.Count == 0) throw new ArgumentException("Aucune nappe calculable à enregistrer.");
        Directory.CreateDirectory(directory);
        var snapshot = new SurfaceSnapshot(Guid.NewGuid().ToString("N"), DateTime.UtcNow, surface);
        var path = Path.Combine(directory, snapshot.Id + ".json");
        var temp = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, ct);
            File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return snapshot;
    }

    public async Task<SurfaceSnapshot?> LoadAsync(string id, CancellationToken ct = default)
    {
        if (!ValidId.IsMatch(id)) return null;
        try
        {
            await using var stream = File.OpenRead(Path.Combine(directory, id + ".json"));
            return await JsonSerializer.DeserializeAsync<SurfaceSnapshot>(stream, JsonOptions, ct);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task<IReadOnlyList<SurfaceSnapshotInfo>> ListAsync(string? currency, CancellationToken ct = default)
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<SurfaceSnapshotInfo>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var s = await LoadAsync(Path.GetFileNameWithoutExtension(file), ct);
                if (s != null && (currency == null || s.Surface.Currency.Equals(currency, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new(s.Id, s.Surface.Currency, s.Surface.AsOfUtc, s.SavedAtUtc, s.Surface.Mode));
            }
            catch (JsonException) { /* Un fichier incomplet ne cache pas les autres observations. */ }
        }
        return result.OrderByDescending(s => s.AsOfUtc).ThenByDescending(s => s.SavedAtUtc).ToArray();
    }
}
