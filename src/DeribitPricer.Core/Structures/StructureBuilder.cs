// Vérifie ce que l'utilisateur a saisi (strikes cotés, ordre, symétrie, quantité) et construit les jambes de la structure.
using System.Globalization;
using DeribitPricer.Core.Common;
using DeribitPricer.Core.Market;

namespace DeribitPricer.Core.Structures;

// Valide une saisie et construit les jambes de la structure.
// Toute incohérence est refusée avec un message explicite.
public static class StructureBuilder
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static BuildResult<StructureDefinition> Build(StructureRequest request, MarketDataStore store)
    {
        var errors = new List<string>();
        var type = request.Type;
        if (type == StructureType.CustomOption)
            return BuildResult<StructureDefinition>.Fail("Les options sur mesure se cotent dans l'écran « Sur mesure ».");

        var expiryCode = Instrument.FormatExpiry(request.Expiry);
        if (!store.GetOptionExpiries(request.Currency).Contains(request.Expiry))
            return BuildResult<StructureDefinition>.Fail($"L'échéance {expiryCode} n'est pas cotée pour {request.Currency}.");

        // Strikes : nombre, cotation, ordre, symétrie.
        int expected = type.StrikeCount();
        var strikes = request.Strikes;
        if (strikes.Count != expected || strikes.Any(k => k <= 0 || double.IsNaN(k)))
            return BuildResult<StructureDefinition>.Fail($"Saisissez {expected} strikes strictement positifs.");

        var optionType = type.OptionType();
        var instruments = new List<Instrument>();
        foreach (var strike in strikes)
        {
            var instrument = store.FindOption(request.Currency, request.Expiry, strike, optionType);
            if (instrument == null)
                errors.Add($"Le strike {strike.ToString("N0", Fr)} n'est pas coté pour l'échéance {expiryCode} " +
                           $"({(optionType == Pricing.OptionType.Call ? "calls" : "puts")}).");
            else
                instruments.Add(instrument);
        }

        for (int i = 1; i < strikes.Count; i++)
        {
            if (strikes[i] <= strikes[i - 1])
            {
                errors.Add(expected == 2
                    ? "Strikes dans le mauvais ordre : il faut K1 < K2."
                    : "Strikes dans le mauvais ordre : il faut K1 < K2 < K3.");
                break;
            }
        }

        if (expected == 3 && strikes[1] > strikes[0] && strikes[2] > strikes[1])
        {
            double left = strikes[1] - strikes[0], right = strikes[2] - strikes[1];
            if (Math.Abs(left - right) > 1e-9)
                errors.Add($"Butterfly non symétrique : K2 − K1 = {left.ToString("N0", Fr)} alors que K3 − K2 = {right.ToString("N0", Fr)}.");
        }

        // Quantité : positive, au moins la quantité minimale et multiple du pas.
        var step = instruments.Count > 0 ? instruments.Max(i => i.MinTradeAmount) : 0.1;
        if (request.Quantity <= 0)
            errors.Add("La quantité doit être strictement positive.");
        else if (request.Quantity < step - 1e-12)
            errors.Add($"Quantité {request.Quantity.ToString("0.####", Fr)} inférieure au minimum de {step.ToString("0.####", Fr)}.");
        else if (!Quantities.IsMultipleOf(request.Quantity, step))
            errors.Add($"Quantité {request.Quantity.ToString("0.####", Fr)} hors pas de cotation : elle doit être un multiple de {step.ToString("0.####", Fr)}.");

        if (errors.Count > 0) return BuildResult<StructureDefinition>.Fail(errors);

        var legs = type switch
        {
            StructureType.CallSpread => new List<LegSpec> { new(instruments[0], +1), new(instruments[1], -1) },
            StructureType.PutSpread => new List<LegSpec> { new(instruments[1], +1), new(instruments[0], -1) },
            _ => new List<LegSpec> { new(instruments[0], +1), new(instruments[1], -2), new(instruments[2], +1) }
        };
        return BuildResult<StructureDefinition>.Ok(new StructureDefinition(request, legs));
    }
}
