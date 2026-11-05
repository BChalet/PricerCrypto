using System.Net.Http;
using System.Text.Json;
using ExcelDna.Integration;

namespace DeribitPricer.Excel;

public static class Functions
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Lazy<PricerApiClient> Client = new(() =>
    {
        var url = Environment.GetEnvironmentVariable("PRICER_API_URL") ?? "http://localhost:5106/";
        var uri = new Uri(url.TrimEnd('/') + "/");
        if (!uri.IsLoopback || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new ArgumentException("PRICER_API_URL doit être une adresse locale HTTP(S) sans identifiants.");
        return new PricerApiClient(Http, uri);
    });

    [ExcelFunction(Name = "PRICEOPTION", Category = "Pricer Deribit",
        Description = "Prix unitaire Black-76 à taux nul. Vol décimale (0,50 = 50 %). Application C# démarrée requise.")]
    public static object PriceOption(
        [ExcelArgument(Description = "Call/C ou Put/P")] string optionType,
        [ExcelArgument(Description = "Date Excel (calendrier 1900) ou AAAA-MM-JJ ; échéance à 08:00 UTC")] object maturity,
        [ExcelArgument(Description = "BTC, ETH...")] string underlying,
        [ExcelArgument(Description = "Strike en USD")] double strike,
        [ExcelArgument(Description = "Optionnel : vol décimale ; omis = nappe de marché")] object volatility,
        [ExcelArgument(Description = "Optionnel : future USD (avec une vol manuelle)")] object forward,
        [ExcelArgument(Description = "USD (défaut) ou COIN")] object unit,
        [ExcelArgument(Description = "Optionnel : identifiant d'instantané, sinon marché actuel")] object snapshotId,
        [ExcelArgument(Description = "Optionnel : cellule compteur ; la changer pour rafraîchir")] object refresh)
        => Run("PRICEOPTION", [optionType, maturity, underlying, strike, volatility, forward, unit, snapshotId, refresh],
            async () => await Client.Value.PriceAsync(optionType, PricerApiClient.Maturity(maturity), underlying, strike,
                OptionalNumber(volatility), OptionalNumber(forward), OptionalText(unit) ?? "USD", OptionalText(snapshotId)));

    [ExcelFunction(Name = "VOLIMPLI", Category = "Pricer Deribit",
        Description = "Vol implicite interpolée au mark, en décimal ; aucune extrapolation.")]
    public static object VolImpli(string underlying, double strike, object maturity, object snapshotId, object refresh)
        => Run("VOLIMPLI", [underlying, strike, maturity, snapshotId, refresh],
            async () => await Client.Value.VolAsync(underlying, strike, PricerApiClient.Maturity(maturity), OptionalText(snapshotId)));

    [ExcelFunction(Name = "VOLSURFACE", Category = "Pricer Deribit",
        Description = "Matrice strikes × échéances (IV décimales), avec date et source. Déversement Excel 365.")]
    public static object VolSurface(string underlying, object snapshotId, object refresh)
        => Run("VOLSURFACE", [underlying, snapshotId, refresh], async () =>
        {
            var table = PricerApiClient.SurfaceTable(await Client.Value.SurfaceAsync(underlying, OptionalText(snapshotId)));
            var result = new object[table.GetLength(0), table.GetLength(1)];
            for (int i = 0; i < result.GetLength(0); i++)
                for (int j = 0; j < result.GetLength(1); j++) result[i, j] = table[i, j] ?? ExcelError.ExcelErrorNA;
            return result;
        });

    // Le travail réseau est asynchrone : pas de blocage du thread de calcul Excel.
    // Le compteur refresh fait partie de la clé et permet de demander une nouvelle observation.
    private static object Run(string name, object[] args, Func<Task<object>> work) =>
        ExcelAsyncUtil.RunTask(name, args, async () =>
        {
            try { return await work(); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            { return "ERREUR PRICER : API locale indisponible. Démarrer l'application, puis changer le compteur de rafraîchissement."; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or InvalidDataException)
            { return "ERREUR PRICER : " + ex.Message; }
        });

    private static bool Missing(object value) => value is ExcelMissing or ExcelEmpty || value is string s && string.IsNullOrWhiteSpace(s);
    private static double? OptionalNumber(object value) => Missing(value) ? null : value is double d ? d :
        throw new ArgumentException("Nombre attendu pour la vol ou le future ; laisser vide pour utiliser le marché.");
    private static string? OptionalText(object value) => Missing(value) ? null : value as string ?? throw new ArgumentException("Texte attendu.");
}
