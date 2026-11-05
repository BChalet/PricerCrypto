// Le profil de P&L d'une structure : grille de -30 % à +30 %, à l'échéance et aujourd'hui, plus gain max, perte max et points morts.
namespace DeribitPricer.Core.Pricing;

// Jambe d'un profil : quantité signée et vol utilisée pour la réévaluation « aujourd'hui ».
public sealed record ProfileLeg(OptionType Type, double Strike, double SignedQuantity, double Vol);

public sealed record ProfilePoint(double ShiftPct, double Spot, double ExpiryPnlUsd, double ExpiryPnlCoin, double TodayPnlUsd);

// Profil de P&L d'une structure sur une grille de prix du sous-jacent.
// La prime payée (en crypto) est convertie en USD au future actuel.
public sealed record PayoffProfile
{
    // Points de la grille demandée (tableau).
    public required IReadOnlyList<ProfilePoint> Points { get; init; }
    // Courbe fine sur la même plage, strikes inclus (graphique).
    public required IReadOnlyList<ProfilePoint> Curve { get; init; }
    public double PremiumCoin { get; init; }
    public double PremiumUsd { get; init; }
    public double Forward { get; init; }
    public double T { get; init; }
    // Gain maximal à l'échéance (null si illimité).
    public double? MaxGainUsd { get; init; }
    // Perte maximale à l'échéance, valeur négative (null si illimitée).
    public double? MaxLossUsd { get; init; }
    public required IReadOnlyList<double> Breakevens { get; init; }
}

public static class PayoffProfileCalculator
{
    // premiumCoin : Prime payée en crypto (négative si reçue).
    public static PayoffProfile Compute(IReadOnlyList<ProfileLeg> legs, double forward, double t, double premiumCoin,
        double minPct = -30, double maxPct = 30, double stepPct = 5)
    {
        if (stepPct <= 0) throw new ArgumentException("Le pas doit être strictement positif.");
        if (minPct >= maxPct) throw new ArgumentException("Le bas de la grille doit être inférieur au haut.");
        if (minPct <= -100) throw new ArgumentException("Le bas de la grille doit être supérieur à −100 %.");

        double premiumUsd = premiumCoin * forward;
        ProfilePoint At(double shift)
        {
            double spot = forward * (1 + shift / 100);
            double payoff = ExpiryPayoffUsd(legs, spot);
            double today = legs.Sum(l => l.SignedQuantity * Black76.PriceUsd(l.Type, spot, l.Strike, l.Vol, t));
            return new ProfilePoint(shift, spot, payoff - premiumUsd, payoff / spot - premiumCoin, today - premiumUsd);
        }

        int n = (int)Math.Round((maxPct - minPct) / stepPct);
        var points = Enumerable.Range(0, n + 1).Select(i => At(minPct + i * stepPct)).ToList();

        var shifts = Enumerable.Range(0, 121).Select(i => minPct + i * (maxPct - minPct) / 120)
            .Concat(legs.Select(l => (l.Strike / forward - 1) * 100).Where(s => s > minPct && s < maxPct))
            .Distinct().OrderBy(s => s);
        var curve = shifts.Select(At).ToList();

        // Extrema et points morts calculés exactement : le payoff est linéaire par morceaux, coudé aux strikes.
        var knots = legs.Select(l => l.Strike).Distinct().OrderBy(k => k).Prepend(0.0).ToList();
        var values = knots.Select(s => ExpiryPayoffUsd(legs, s) - premiumUsd).ToList();
        double slopeAtInfinity = legs.Where(l => l.Type == OptionType.Call).Sum(l => l.SignedQuantity);

        double max = values.Max(), min = values.Min();
        bool gainUnbounded = slopeAtInfinity > 1e-12, lossUnbounded = slopeAtInfinity < -1e-12;

        var breakevens = new List<double>();
        for (int i = 0; i < knots.Count - 1; i++)
        {
            double x0 = knots[i], x1 = knots[i + 1], y0 = values[i], y1 = values[i + 1];
            if (Math.Abs(y0) < 1e-9 && x0 > 0) AddUnique(breakevens, x0);
            else if (y0 * y1 < 0) AddUnique(breakevens, x0 + (x1 - x0) * (-y0) / (y1 - y0));
        }
        double lastX = knots[^1], lastY = values[^1];
        if (Math.Abs(lastY) < 1e-9 && lastX > 0) AddUnique(breakevens, lastX);
        else if (Math.Abs(slopeAtInfinity) > 1e-12)
        {
            double root = lastX - lastY / slopeAtInfinity;
            if (root > lastX) AddUnique(breakevens, root);
        }

        return new PayoffProfile
        {
            Points = points,
            Curve = curve,
            PremiumCoin = premiumCoin,
            PremiumUsd = premiumUsd,
            Forward = forward,
            T = t,
            MaxGainUsd = gainUnbounded ? null : max,
            MaxLossUsd = lossUnbounded ? null : min,
            Breakevens = breakevens
        };
    }

    public static double ExpiryPayoffUsd(IReadOnlyList<ProfileLeg> legs, double spot) =>
        legs.Sum(l => l.SignedQuantity * Black76.Intrinsic(l.Type, spot, l.Strike));

    private static void AddUnique(List<double> list, double x)
    {
        if (!list.Any(v => Math.Abs(v - x) < 1e-6)) list.Add(x);
    }
}
