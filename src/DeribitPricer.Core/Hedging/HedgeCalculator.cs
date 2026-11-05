// La couverture d'une option sur mesure : deux options pour annuler gamma et vega, un future pour le delta, puis arrondis, coût et prix client.
using System.Globalization;
using DeribitPricer.Core.Common;
using DeribitPricer.Core.Market;
using DeribitPricer.Core.Pricing;

namespace DeribitPricer.Core.Hedging;

// Un instrument de couverture avec sa quantité signée (en unités de sous-jacent).
public sealed record HedgeLine
{
    public required Instrument Instrument { get; init; }
    // Solution exacte du système, avant arrondi.
    public double TheoreticalQuantity { get; init; }
    // Quantité arrondie au pas de cotation.
    public double Quantity { get; init; }
    // Prix exécutable dans le sens de la quantité (crypto pour une option, USD pour un future).
    public double ExecPrice { get; init; }
    public double MidPrice { get; init; }
    public double ExecPriceUsd { get; init; }
    public double MidPriceUsd { get; init; }
    // |quantité| x |exécutable − mid| en USD.
    public double CostUsd { get; init; }
    public Greeks UnitGreeks { get; init; }
    public double Forward { get; init; }
    // Vol utilisée (décimal), 0 pour un future.
    public double Vol { get; init; }
    public double QuantityStep { get; init; }
    public bool IsFuture => Instrument.Kind == InstrumentKind.Future;
}

public sealed record HedgeResult
{
    public required CustomOptionValuation Custom { get; init; }
    // q0 : quantité signée de l'option sur mesure du point de vue du desk.
    public double CustomQuantity { get; init; }
    public required IReadOnlyList<HedgeLine> Lines { get; init; }
    public Greeks CustomGreeks { get; init; }
    public Greeks HedgeGreeks { get; init; }
    public Greeks GlobalGreeks { get; init; }
    public Greeks GlobalGreeksBeforeRounding { get; init; }
    public double HedgeCostUsd { get; init; }
    public double HedgeCostCoin => HedgeCostUsd / Custom.Forward;
    public double RelativeDeterminant { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

// Couverture d'une option sur mesure (annexe C) : deux options cotées neutralisent gamma et vega,
// puis un future neutralise le delta.
public static class HedgeCalculator
{
    // |det| / (|Γ1 V2| + |Γ2 V1|) en dessous duquel la paire est refusée : les deux options ont alors
    // presque le même rapport gamma/vega et les quantités exploseraient.
    public const double MinRelativeDeterminant = 0.02;

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static BuildResult<HedgeResult> Compute(CustomOptionValuation custom, double q0,
        Instrument option1, Instrument option2, Instrument future, MarketDataStore store)
    {
        var errors = new List<string>();
        if (!option1.IsOption || !option2.IsOption) errors.Add("Les deux instruments de couverture gamma/vega doivent être des options.");
        if (option1.Name == option2.Name) errors.Add("Choisissez deux options différentes.");
        if (future.Kind != InstrumentKind.Future) errors.Add("Le delta se couvre avec un future.");
        if (Quantities.IsZero(q0)) errors.Add("La quantité de l'option sur mesure est nulle.");
        if (errors.Count > 0) return BuildResult<HedgeResult>.Fail(errors);

        var a1 = OptionAnalytics.Compute(option1, store.GetQuote(option1.Name));
        var a2 = OptionAnalytics.Compute(option2, store.GetQuote(option2.Name));
        if (a1?.Greeks == null) errors.Add($"{option1.Name} : pas de cotation exploitable.");
        if (a2?.Greeks == null) errors.Add($"{option2.Name} : pas de cotation exploitable.");
        var futureQuote = store.GetQuote(future.Name);
        if (futureQuote == null) errors.Add($"{future.Name} : pas de cotation.");
        if (errors.Count > 0) return BuildResult<HedgeResult>.Fail(errors);

        Greeks g0 = custom.UnitGreeks, g1 = a1!.Greeks!.Value, g2 = a2!.Greeks!.Value;

        // Système 2x2 : q1 Γ1 + q2 Γ2 = −q0 Γ0 ; q1 V1 + q2 V2 = −q0 V0.
        double det = g1.Gamma * g2.Vega - g2.Gamma * g1.Vega;
        double scale = Math.Abs(g1.Gamma * g2.Vega) + Math.Abs(g2.Gamma * g1.Vega);
        double relDet = scale > 0 ? Math.Abs(det) / scale : 0;
        if (relDet < MinRelativeDeterminant)
            return BuildResult<HedgeResult>.Fail(
                $"Paire refusée : les deux options ont presque le même rapport gamma/vega (déterminant relatif {relDet:P2} < {MinRelativeDeterminant:P0}), " +
                "le système n'a pas de solution exploitable. Γ/V = 1/(F²σT) ne dépend que de la maturité et de la vol : " +
                "prenez deux options d'échéances différentes.");

        double b1 = -q0 * g0.Gamma, b2 = -q0 * g0.Vega;
        double q1 = (b1 * g2.Vega - g2.Gamma * b2) / det;
        double q2 = (g1.Gamma * b2 - g1.Vega * b1) / det;

        double step1 = option1.MinTradeAmount, step2 = option2.MinTradeAmount;
        double q1r = Quantities.RoundToStep(q1, step1), q2r = Quantities.RoundToStep(q2, step2);

        // Future (delta 1 par unité de sous-jacent) : qF = −(q0 Δ0 + q1 Δ1 + q2 Δ2), avec les quantités arrondies.
        double futurePrice = futureQuote!.Mark ?? futureQuote.Mid ?? futureQuote.UnderlyingPrice;
        double futureStep = future.QuantityStepInCoin(futurePrice);
        double qfExact = -(q0 * g0.Delta + q1 * g1.Delta + q2 * g2.Delta);
        double qf = -(q0 * g0.Delta + q1r * g1.Delta + q2r * g2.Delta);
        double qfr = Quantities.RoundToStep(qf, futureStep);

        var warnings = new List<string>();
        var lines = new List<HedgeLine>();
        AddOptionLine(lines, errors, a1, q1, q1r, step1);
        AddOptionLine(lines, errors, a2, q2, q2r, step2);
        AddFutureLine(lines, errors, future, futureQuote, qf, qfr, futureStep);
        if (errors.Count > 0) return BuildResult<HedgeResult>.Fail(errors);

        foreach (var line in lines.Where(l => Quantities.IsZero(l.Quantity) && !Quantities.IsZero(l.TheoreticalQuantity)))
            warnings.Add($"{line.Instrument.Name} : quantité arrondie à 0 (pas de {line.QuantityStep.ToString("0.####", Fr)}).");

        var customGreeks = q0 * g0;
        var hedgeGreeks = Greeks.Sum(lines.Select(l => l.Quantity * l.UnitGreeks));
        var exactHedge = q1 * g1 + q2 * g2 + new Greeks(qfExact, 0, 0, 0);

        return BuildResult<HedgeResult>.Ok(new HedgeResult
        {
            Custom = custom,
            CustomQuantity = q0,
            Lines = lines,
            CustomGreeks = customGreeks,
            HedgeGreeks = hedgeGreeks,
            GlobalGreeks = customGreeks + hedgeGreeks,
            GlobalGreeksBeforeRounding = customGreeks + exactHedge,
            HedgeCostUsd = lines.Sum(l => l.CostUsd),
            RelativeDeterminant = relDet,
            Warnings = warnings
        });
    }

    private static void AddOptionLine(List<HedgeLine> lines, List<string> errors, OptionAnalytics a, double exact, double rounded, double step)
    {
        var quote = a.Quote;
        double? exec = rounded >= 0 ? quote.Ask : quote.Bid;
        if (!Quantities.IsZero(rounded) && !exec.HasValue)
        {
            errors.Add($"{a.Instrument.Name} : pas de {(rounded > 0 ? "ask" : "bid")}, couverture non traitable.");
            return;
        }
        double mid = quote.Mid ?? quote.Mark ?? 0;
        double execPrice = exec ?? mid;
        lines.Add(new HedgeLine
        {
            Instrument = a.Instrument,
            TheoreticalQuantity = exact,
            Quantity = rounded,
            ExecPrice = execPrice,
            MidPrice = mid,
            ExecPriceUsd = execPrice * a.Forward,
            MidPriceUsd = mid * a.Forward,
            CostUsd = Math.Abs(rounded) * Math.Abs(execPrice - mid) * a.Forward,
            UnitGreeks = a.Greeks!.Value,
            Forward = a.Forward,
            Vol = a.Vol ?? 0,
            QuantityStep = step
        });
    }

    private static void AddFutureLine(List<HedgeLine> lines, List<string> errors, Instrument future, MarketQuote quote,
        double exact, double rounded, double step)
    {
        double? exec = rounded >= 0 ? quote.Ask : quote.Bid;
        if (!Quantities.IsZero(rounded) && !exec.HasValue)
        {
            errors.Add($"{future.Name} : pas de {(rounded > 0 ? "ask" : "bid")}, couverture non traitable.");
            return;
        }
        double mid = quote.Mid ?? quote.Mark ?? 0;
        double execPrice = exec ?? mid;
        lines.Add(new HedgeLine
        {
            Instrument = future,
            TheoreticalQuantity = exact,
            Quantity = rounded,
            ExecPrice = execPrice,
            MidPrice = mid,
            ExecPriceUsd = execPrice,
            MidPriceUsd = mid,
            CostUsd = Math.Abs(rounded) * Math.Abs(execPrice - mid),
            UnitGreeks = new Greeks(1, 0, 0, 0),
            Forward = mid,
            QuantityStep = step
        });
    }

    // Prix proposé au client, par option : prix théorique, plus (client acheteur) ou moins (client vendeur)
    // le coût de couverture réparti sur la quantité et la marge du desk.
    public static double ClientPriceUsd(CustomOptionValuation custom, double q0, double hedgeCostUsd, double marginUsd)
    {
        double sign = q0 < 0 ? 1 : -1;  // le desk vend (q0 < 0) : le client paie plus cher
        double costPerUnit = Quantities.IsZero(q0) ? 0 : hedgeCostUsd / Math.Abs(q0);
        return custom.TheoUsd + sign * (costPerUnit + marginUsd);
    }
}
