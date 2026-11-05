using DeribitPricer.Core.Common;
using DeribitPricer.Core.Market;

namespace DeribitPricer.Core.Pricing;

public sealed record VolatilityPoint(double Strike, double Vol);
public sealed record VolatilitySlice(DateTime ExpiryUtc, double Forward, DateTime? OldestQuoteUtc,
    DateTime? LatestQuoteUtc, IReadOnlyList<VolatilityPoint> Points);
public sealed record SurfaceValue(double Vol, double Forward, double T, double LogMoneyness,
    bool OnListedExpiry, IReadOnlyList<SmileSlice> Slices);

// Vols en décimal. Une surface figée évite de recalculer tous les smiles pour chaque cellule Excel.
public sealed record VolatilitySurface(string Currency, DateTime AsOfUtc, DateTime? LastEventUtc,
    string Mode, double? IndexPrice, IReadOnlyList<VolatilitySlice> Slices)
{
    public static VolatilitySurface Capture(MarketDataStore store, string currency)
    {
        currency = currency.Trim().ToUpperInvariant();
        var now = store.Now;
        var slices = new List<VolatilitySlice>();
        foreach (var expiry in store.GetOptionExpiries(currency).Where(e => e > now))
        {
            var forward = store.GetForward(currency, expiry);
            if (forward is not > 0 || !double.IsFinite(forward.Value)) continue;
            var points = CustomOptionPricer.MarkSmile(store, currency, expiry)
                .Where(p => double.IsFinite(p.Strike) && p.Strike > 0 && double.IsFinite(p.Vol) && p.Vol > 0)
                .Select(p => new VolatilityPoint(p.Strike, p.Vol)).ToArray();
            if (points.Length < 2) continue;
            var timestamps = store.GetOptions(currency, expiry).Select(o => store.GetQuote(o.Name))
                .Where(q => q?.Mark is > 0).Select(q => q!.TimestampUtc).ToArray();
            slices.Add(new(expiry, forward.Value, timestamps.Length == 0 ? null : timestamps.Min(),
                timestamps.Length == 0 ? null : timestamps.Max(), points));
        }
        return new(currency, now, store.LastEventUtc, store.Status.Mode, store.GetIndex(currency), slices);
    }

    // Même convention que le pricer sur mesure : F linéaire, smile linéaire en ln(K/F),
    // variance totale linéaire en temps. Aucune extrapolation en strike ou maturité.
    public BuildResult<SurfaceValue> Value(double strike, DateTime expiryUtc)
    {
        if (!double.IsFinite(strike) || strike <= 0)
            return BuildResult<SurfaceValue>.Fail("Le strike doit être strictement positif et fini.");
        if (expiryUtc <= AsOfUtc) return BuildResult<SurfaceValue>.Fail("L'échéance est déjà passée.");
        if (Slices.Count == 0) return BuildResult<SurfaceValue>.Fail($"Aucune nappe disponible pour {Currency}.");
        var exact = Slices.FirstOrDefault(s => s.ExpiryUtc == expiryUtc);
        var left = exact ?? Slices.LastOrDefault(s => s.ExpiryUtc < expiryUtc);
        var right = exact ?? Slices.FirstOrDefault(s => s.ExpiryUtc > expiryUtc);
        if (left == null || right == null)
            return BuildResult<SurfaceValue>.Fail("Échéance hors de la plage cotée ; aucune extrapolation.");
        var t = Black76.YearFraction(expiryUtc, AsOfUtc);
        var t1 = Black76.YearFraction(left.ExpiryUtc, AsOfUtc);
        var t2 = Black76.YearFraction(right.ExpiryUtc, AsOfUtc);
        var weight = exact != null ? 0 : (t - t1) / (t2 - t1);
        var forward = left.Forward + weight * (right.Forward - left.Forward);
        var k = Math.Log(strike / forward);
        var selected = exact != null ? new[] { left } : new[] { left, right };
        var details = new List<SmileSlice>();
        foreach (var slice in selected)
        {
            var equivalentStrike = slice.Forward * Math.Exp(k);
            var points = slice.Points;
            // Tolérance uniquement aux erreurs d'arrondi sur un strike coté.
            if (equivalentStrike < points[0].Strike * (1 - 1e-12) || equivalentStrike > points[^1].Strike * (1 + 1e-12))
                return BuildResult<SurfaceValue>.Fail($"Strike hors de la plage cotée de {Instrument.FormatExpiry(slice.ExpiryUtc)} ; aucune extrapolation.");
            equivalentStrike = Math.Clamp(equivalentStrike, points[0].Strike, points[^1].Strike);
            var b = 0;
            while (b < points.Count - 1 && points[b].Strike < equivalentStrike) b++;
            var a = Math.Abs(points[b].Strike - equivalentStrike) < 1e-8 ? b : Math.Max(0, b - 1);
            var va = points[a];
            var vb = points[b];
            var vol = a == b ? va.Vol : va.Vol + Math.Log(equivalentStrike / va.Strike) /
                Math.Log(vb.Strike / va.Strike) * (vb.Vol - va.Vol);
            details.Add(new(slice.ExpiryUtc, Black76.YearFraction(slice.ExpiryUtc, AsOfUtc), slice.Forward,
                equivalentStrike, va.Strike, vb.Strike, va.Vol, vb.Vol, vol));
        }
        var variance = details[0].TotalVariance;
        if (exact == null) variance += weight * (details[1].TotalVariance - variance);
        var sigma = Math.Sqrt(variance / t);
        if (!double.IsFinite(sigma) || sigma <= 0) return BuildResult<SurfaceValue>.Fail("Variance interpolée invalide.");
        return BuildResult<SurfaceValue>.Ok(new(sigma, forward, t, k, exact != null, details));
    }

    public double? ForwardAt(DateTime expiryUtc)
    {
        var exact = Slices.FirstOrDefault(s => s.ExpiryUtc == expiryUtc);
        if (exact != null) return exact.Forward;
        var a = Slices.LastOrDefault(s => s.ExpiryUtc < expiryUtc);
        var b = Slices.FirstOrDefault(s => s.ExpiryUtc > expiryUtc);
        if (a == null || b == null || expiryUtc <= AsOfUtc) return null;
        return a.Forward + (expiryUtc - a.ExpiryUtc).TotalSeconds / (b.ExpiryUtc - a.ExpiryUtc).TotalSeconds * (b.Forward - a.Forward);
    }

    public double? VolAt(double tenorDays, double moneyness)
    {
        if (!double.IsFinite(tenorDays) || tenorDays <= 0 || tenorDays > 36500 ||
            !double.IsFinite(moneyness) || moneyness <= 0) return null;
        var expiry = AsOfUtc.AddDays(tenorDays);
        var forward = ForwardAt(expiry);
        return forward.HasValue ? Value(forward.Value * moneyness, expiry).Value?.Vol : null;
    }
}
