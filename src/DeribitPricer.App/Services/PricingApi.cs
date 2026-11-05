using System.Globalization;
using DeribitPricer.Core.Market;
using DeribitPricer.Core.Pricing;

namespace DeribitPricer.App.Services;

public static class PricingApi
{
    public static void MapPricingApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/priceoption", async (string optionType, string maturity, string underlying, double strike,
            double? volatility, double? forward, string? unit, string? snapshotId, VolatilityService surfaces, CancellationToken ct) =>
        {
            if (!TryType(optionType, out var type)) return Error("Type attendu : Call (C) ou Put (P).");
            if (!TryMaturity(maturity, out var expiry)) return Error("Maturité attendue : AAAA-MM-JJ (08:00 UTC), ou date-heure ISO avec fuseau.");
            if (!double.IsFinite(strike) || strike <= 0) return Error("Strike strictement positif et fini requis.");
            if (volatility.HasValue && (!double.IsFinite(volatility.Value) || volatility.Value < 0))
                return Error("Volatilité en décimal positive ou nulle requise (0.50 = 50 %).");
            if (forward.HasValue && (!double.IsFinite(forward.Value) || forward.Value <= 0))
                return Error("Future strictement positif et fini requis.");
            unit = (unit ?? "USD").Trim().ToUpperInvariant();
            if (unit is not ("USD" or "COIN")) return Error("Unité attendue : USD ou COIN.");
            if (forward.HasValue && !volatility.HasValue)
                return Error("Un future manuel nécessite aussi une volatilité manuelle, pour ne pas mélanger les scénarios.");
            var surface = await surfaces.GetAsync(underlying, snapshotId, ct);
            if (surface == null) return Results.NotFound(new { error = "Instantané introuvable pour ce sous-jacent." });
            var now = surface.AsOfUtc;
            if (expiry < now) return Error("L'échéance est déjà passée à la date de valorisation.");
            double sigma, f;
            if (volatility.HasValue)
            {
                sigma = volatility.Value;
                var marketForward = forward ?? surface.ForwardAt(expiry);
                if (marketForward is not > 0) return Error("Future indisponible : fournir un future avec la volatilité manuelle.");
                f = marketForward.Value;
            }
            else
            {
                var v = surface.Value(strike, expiry);
                if (!v.IsSuccess) return Error(string.Join(" ", v.Errors));
                sigma = v.Value!.Vol;
                f = v.Value.Forward;
            }
            var t = Black76.YearFraction(expiry, now);
            var usd = Black76.PriceUsd(type, f, strike, sigma, t);
            if (!double.IsFinite(usd)) return Error("Paramètres hors des limites numériques du modèle.");
            return Results.Ok(new { price = unit == "USD" ? usd : usd / f, unit, priceUsd = usd, priceCoin = usd / f,
                vol = sigma, forward = f, expiryUtc = expiry, asOfUtc = now, underlying = surface.Currency,
                mode = surface.Mode, source = volatility.HasValue ? "ManualVol" : "MarketSurface", snapshotId,
                model = "Black76, taux nul, ACT/365, prix unitaire" });
        });

        api.MapGet("/volimpli", async (string underlying, double strike, string maturity, string? snapshotId,
            VolatilityService surfaces, CancellationToken ct) =>
        {
            if (!TryMaturity(maturity, out var expiry)) return Error("Maturité invalide : utiliser AAAA-MM-JJ ou ISO UTC.");
            var surface = await surfaces.GetAsync(underlying, snapshotId, ct);
            if (surface == null) return Results.NotFound(new { error = "Instantané introuvable pour ce sous-jacent." });
            var v = surface.Value(strike, expiry);
            return v.IsSuccess
                ? Results.Ok(new { vol = v.Value!.Vol, forward = v.Value.Forward, surface.AsOfUtc, surface.Mode, snapshotId,
                    interpolated = !v.Value.OnListedExpiry || v.Value.Slices.Any(s => s.StrikeA != s.StrikeB) })
                : Error(string.Join(" ", v.Errors));
        });

        api.MapGet("/volsurface", async (string underlying, string? snapshotId, VolatilityService surfaces, CancellationToken ct) =>
        {
            var surface = await surfaces.GetAsync(underlying, snapshotId, ct);
            if (surface == null) return Results.NotFound(new { error = "Instantané introuvable pour ce sous-jacent." });
            if (surface.Slices.Count == 0) return Error("Aucune nappe disponible pour ce sous-jacent.");
            return Results.Ok(surface);
        });
        api.MapGet("/snapshots", async (string underlying, SurfaceSnapshotRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListAsync(underlying, ct)));
        // Les captures se font depuis l'interface Blazor. L'API Excel est entièrement en lecture seule.
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
    }

    private static IResult Error(string message) => Results.BadRequest(new { error = message });

    private static bool TryType(string value, out OptionType type)
    {
        switch (value.Trim().ToUpperInvariant())
        {
            case "C": case "CALL": type = OptionType.Call; return true;
            case "P": case "PUT": type = OptionType.Put; return true;
            default: type = default; return false;
        }
    }

    public static bool TryMaturity(string value, out DateTime utc)
    {
        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            utc = DateTime.SpecifyKind(date.AddHours(8), DateTimeKind.Utc);
            return true;
        }
        if (value.Contains('T') && (value.EndsWith('Z') || value.LastIndexOf('+') > 10 || value.LastIndexOf('-') > 10) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
        {
            utc = timestamp.UtcDateTime;
            return true;
        }
        utc = default;
        return false;
    }
}
