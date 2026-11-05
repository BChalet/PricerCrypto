// L'option sur mesure (annexe C) : on interpole le future et la vol à partir des options cotées, en quatre étapes.
using System.Globalization;
using DeribitPricer.Core.Common;
using DeribitPricer.Core.Market;

namespace DeribitPricer.Core.Pricing;

// Option sur mesure : strike et échéance libres, non cotée sur Deribit.
public sealed record CustomOptionSpec(string Currency, OptionType Type, double Strike, DateTime ExpiryUtc)
{
    public string Name =>
        $"OTC-{Currency}-{Instrument.FormatExpiry(ExpiryUtc)}-{Strike.ToString("0.##", CultureInfo.InvariantCulture)}-{(Type == OptionType.Call ? "C" : "P")}";
}

// Détail de l'étape 3 de l'annexe C pour une échéance cotée.
public sealed record SmileSlice(
    DateTime Expiry,
    double T,
    double Forward,
    double EquivalentStrike,
    double StrikeA,
    double StrikeB,
    double VolA,
    double VolB,
    double Vol)
{
    public double TotalVariance => Vol * Vol * T;
}

public sealed record CustomOptionValuation
{
    public required CustomOptionSpec Spec { get; init; }
    public DateTime AsOf { get; init; }
    public double T { get; init; }
    // Future interpolé F(T).
    public double Forward { get; init; }
    // k = ln(K / F(T)).
    public double LogMoneyness { get; init; }
    // Vol interpolée en décimal.
    public double Vol { get; init; }
    public bool OnListedExpiry { get; init; }
    public required IReadOnlyList<SmileSlice> Slices { get; init; }
    public double TheoUsd { get; init; }
    public double TheoCoin { get; init; }
    public Greeks UnitGreeks { get; init; }
}

// Interpolation du future et de la vol d'une option sur mesure (annexe C) :
// future linéaire en temps, vol linéaire en log-moneyness sur chaque échéance,
// puis variance totale linéaire en temps entre les deux échéances encadrantes.
public static class CustomOptionPricer
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static BuildResult<CustomOptionValuation> Value(CustomOptionSpec spec, MarketDataStore store)
    {
        if (spec.Strike <= 0) return BuildResult<CustomOptionValuation>.Fail("Le strike doit être strictement positif.");
        var now = store.Now;
        if (spec.ExpiryUtc <= now) return BuildResult<CustomOptionValuation>.Fail("L'échéance est déjà passée.");

        var expiries = store.GetOptionExpiries(spec.Currency).Where(e => e > now).ToList();
        if (expiries.Count == 0) return BuildResult<CustomOptionValuation>.Fail($"Aucune échéance cotée pour {spec.Currency}.");

        double t = Black76.YearFraction(spec.ExpiryUtc, now);
        bool listed = expiries.Contains(spec.ExpiryUtc);
        DateTime e1, e2;
        if (listed)
        {
            e1 = e2 = spec.ExpiryUtc;
        }
        else
        {
            var before = expiries.Where(e => e < spec.ExpiryUtc).ToList();
            var after = expiries.Where(e => e > spec.ExpiryUtc).ToList();
            if (before.Count == 0 || after.Count == 0)
                return BuildResult<CustomOptionValuation>.Fail(
                    $"Échéance hors de la plage cotée : elle doit être comprise entre le {expiries[0]:dd/MM/yyyy} et le {expiries[^1]:dd/MM/yyyy}.");
            e1 = before.Max();
            e2 = after.Min();
        }

        double t1 = Black76.YearFraction(e1, now), t2 = Black76.YearFraction(e2, now);
        var f1 = store.GetForward(spec.Currency, e1);
        var f2 = store.GetForward(spec.Currency, e2);
        if (f1 is not > 0 || f2 is not > 0)
            return BuildResult<CustomOptionValuation>.Fail("Future indisponible sur une échéance encadrante.");

        // Étape 1 : future interpolé linéairement en temps.
        double w = listed ? 0 : (t - t1) / (t2 - t1);
        double forward = f1.Value + w * (f2.Value - f1.Value);

        // Étape 2 : log-moneyness.
        double k = Math.Log(spec.Strike / forward);

        // Étape 3 : vol de même log-moneyness sur chaque échéance cotée.
        var slices = new List<SmileSlice>();
        var errors = new List<string>();
        foreach (var (expiry, ti, fi) in listed ? [(e1, t1, f1.Value)] : new[] { (e1, t1, f1.Value), (e2, t2, f2.Value) })
        {
            var slice = SliceAt(store, spec.Currency, expiry, ti, fi, k);
            if (slice.IsSuccess) slices.Add(slice.Value!);
            else errors.AddRange(slice.Errors);
        }
        if (errors.Count > 0) return BuildResult<CustomOptionValuation>.Fail(errors);

        // Étape 4 : variance totale interpolée linéairement en temps.
        double vol;
        if (listed)
        {
            vol = slices[0].Vol;
        }
        else
        {
            double totalVariance = slices[0].TotalVariance + w * (slices[1].TotalVariance - slices[0].TotalVariance);
            if (totalVariance <= 0) return BuildResult<CustomOptionValuation>.Fail("Variance totale interpolée négative.");
            vol = Math.Sqrt(totalVariance / t);
        }

        double theoUsd = Black76.PriceUsd(spec.Type, forward, spec.Strike, vol, t);
        return BuildResult<CustomOptionValuation>.Ok(new CustomOptionValuation
        {
            Spec = spec,
            AsOf = now,
            T = t,
            Forward = forward,
            LogMoneyness = k,
            Vol = vol,
            OnListedExpiry = listed,
            Slices = slices,
            TheoUsd = theoUsd,
            TheoCoin = theoUsd / forward,
            UnitGreeks = Black76.ComputeGreeks(spec.Type, forward, spec.Strike, vol, t)
        });
    }

    // Étape 3 : strike équivalent Ki = Fi e^k, encadrement par deux strikes cotés, interpolation en log-moneyness.
    public static BuildResult<SmileSlice> SliceAt(MarketDataStore store, string currency, DateTime expiry, double ti, double fi, double k)
    {
        double ki = fi * Math.Exp(k);
        var smile = MarkSmile(store, currency, expiry);
        var code = Instrument.FormatExpiry(expiry);
        if (smile.Count < 2)
            return BuildResult<SmileSlice>.Fail($"Pas assez de vols calculables sur l'échéance {code}.");
        if (ki < smile[0].Strike || ki > smile[^1].Strike)
            return BuildResult<SmileSlice>.Fail(
                $"Strike équivalent {ki.ToString("N0", Fr)} hors de la plage des strikes cotés de l'échéance {code} " +
                $"[{smile[0].Strike.ToString("N0", Fr)} ; {smile[^1].Strike.ToString("N0", Fr)}] : option refusée.");

        int b = smile.FindIndex(p => p.Strike >= ki);
        int a = Math.Max(0, b - 1);
        if (smile[b].Strike == ki) a = b;  // strike coté exactement
        var (ka, va) = smile[a];
        var (kb, vb) = smile[b];

        double vol;
        if (a == b)
        {
            vol = va;
        }
        else
        {
            double kaLog = Math.Log(ka / fi), kbLog = Math.Log(kb / fi);
            vol = va + (k - kaLog) / (kbLog - kaLog) * (vb - va);
        }
        return BuildResult<SmileSlice>.Ok(new SmileSlice(expiry, ti, fi, ki, ka, kb, va, vb, vol));
    }

    // Smile d'une échéance : vol calculée par notre inversion à partir du mark de l'option hors de la monnaie
    // (put sous le future, call au-dessus), plus précise numériquement. Trié par strike.
    public static List<(double Strike, double Vol)> MarkSmile(MarketDataStore store, string currency, DateTime expiry)
    {
        var result = new List<(double, double)>();
        foreach (var group in store.GetOptions(currency, expiry).GroupBy(o => o.Strike).OrderBy(g => g.Key))
        {
            double? vol = null;
            foreach (var option in group.OrderBy(o => o.OptionType))
            {
                var quote = store.GetQuote(option.Name);
                if (quote?.Mark is not > 0 || quote.UnderlyingPrice <= 0) continue;
                bool otm = option.OptionType == OptionType.Call ? option.Strike >= quote.UnderlyingPrice : option.Strike < quote.UnderlyingPrice;
                if (!otm && vol.HasValue) continue;
                double t = Black76.YearFraction(option.ExpiryUtc, quote.TimestampUtc);
                var iv = ImpliedVolSolver.SolveFromCoinPrice(option.OptionType!.Value, quote.Mark.Value, quote.UnderlyingPrice, option.Strike, t);
                if (iv.Success && (otm || !vol.HasValue)) vol = iv.Vol;
            }
            if (vol.HasValue) result.Add((group.Key, vol.Value));
        }
        return result;
    }
}
