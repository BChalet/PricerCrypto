using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using DeribitPricer.Core.Pricing;

namespace DeribitPricer.Excel;

// Transport indépendant d'Excel : testé avec la même API que le complément Windows.
public sealed class PricerApiClient(HttpClient http, Uri baseUri)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<double> PriceAsync(string optionType, string maturity, string underlying, double strike,
        double? volatility = null, double? forward = null, string unit = "USD", string? snapshotId = null)
    {
        using var json = await GetAsync("priceoption", new()
        {
            ["optionType"] = optionType, ["maturity"] = maturity, ["underlying"] = underlying, ["strike"] = Number(strike),
            ["volatility"] = volatility.HasValue ? Number(volatility.Value) : null,
            ["forward"] = forward.HasValue ? Number(forward.Value) : null, ["unit"] = unit, ["snapshotId"] = snapshotId
        });
        return json.RootElement.GetProperty("price").GetDouble();
    }

    public async Task<double> VolAsync(string underlying, double strike, string maturity, string? snapshotId = null)
    {
        using var json = await GetAsync("volimpli", new()
        {
            ["underlying"] = underlying, ["strike"] = Number(strike), ["maturity"] = maturity, ["snapshotId"] = snapshotId
        });
        return json.RootElement.GetProperty("vol").GetDouble();
    }

    public async Task<VolatilitySurface> SurfaceAsync(string underlying, string? snapshotId = null)
    {
        using var json = await GetAsync("volsurface", new() { ["underlying"] = underlying, ["snapshotId"] = snapshotId });
        return json.RootElement.Deserialize<VolatilitySurface>(JsonOptions) ?? throw new InvalidDataException("Nappe vide.");
    }

    // Matrice déversée : colonnes = échéances, lignes = strikes (pas de grille artificielle hors couverture).
    public static object?[,] SurfaceTable(VolatilitySurface surface)
    {
        var strikes = surface.Slices.SelectMany(s => s.Points.Select(p => p.Strike)).Distinct().Order().ToArray();
        var table = new object?[strikes.Length + 2, surface.Slices.Count + 1];
        table[0, 0] = $"{surface.Currency} · {surface.Mode} · {surface.AsOfUtc:yyyy-MM-dd HH:mm:ss} UTC · IV décimale";
        table[1, 0] = "Future (USD) / Strike (USD)";
        for (int j = 0; j < surface.Slices.Count; j++)
        {
            var slice = surface.Slices[j];
            table[0, j + 1] = slice.ExpiryUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
            table[1, j + 1] = slice.Forward;
            for (int i = 0; i < strikes.Length; i++)
                table[i + 2, j + 1] = surface.Value(strikes[i], slice.ExpiryUtc).Value?.Vol;
        }
        for (int i = 0; i < strikes.Length; i++) table[i + 2, 0] = strikes[i];
        return table;
    }

    // Excel transmet une date comme un numéro de série OLE Automation (classeur au calendrier 1900).
    public static string Maturity(object maturity) => maturity switch
    {
        double serial when double.IsFinite(serial) => DateTime.FromOADate(serial).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        string text when !string.IsNullOrWhiteSpace(text) => text.Trim(),
        _ => throw new ArgumentException("Maturité : cellule date Excel (calendrier 1900) ou texte ISO AAAA-MM-JJ.")
    };

    private async Task<JsonDocument> GetAsync(string method, Dictionary<string, string?> args)
    {
        var query = string.Join("&", args.Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value!)));
        using var response = await http.GetAsync(new Uri(baseUri, "api/" + method + "?" + query));
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        if (!response.IsSuccessStatusCode)
        {
            var message = json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : $"API HTTP {(int)response.StatusCode}";
            json.Dispose();
            throw new InvalidOperationException(message);
        }
        return json;
    }
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
