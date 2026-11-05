// Retrouve la vol implicite à partir d'un prix (annexe A) : on vérifie les bornes, Newton, et dichotomie si Newton ne s'en sort pas.
namespace DeribitPricer.Core.Pricing;

// Résultat d'une inversion de Black-76. Vol est null si elle est impossible à calculer.
public readonly record struct ImpliedVolResult(double? Vol, string? Error, int Iterations, string Method)
{
    public bool Success => Vol.HasValue;
}

// Inversion de Black-76 (annexe A) : Newton à partir de sigma0, avec bascule sur une dichotomie
// dans [0,01 % ; 500 %] si le vega est trop petit ou si sigma sort de l'intervalle.
public static class ImpliedVolSolver
{
    public const double MinVol = 0.0001;          // 0,01 %
    public const double MaxVol = 5.0;             // 500 %
    public const double PriceTolerance = 1e-6;    // USD
    public const double DefaultInitialVol = 0.5;  // 50 %
    private const double MinVega = 1e-8;
    private const int MaxNewtonIterations = 100;
    private const int MaxBisectionIterations = 200;

    // Vol implicite à partir d'un prix exprimé en crypto (convention Deribit).
    public static ImpliedVolResult SolveFromCoinPrice(OptionType type, double priceCoin, double f, double k, double t) =>
        Solve(type, priceCoin * f, f, k, t);

    // Vol implicite à partir d'un prix en USD.
    public static ImpliedVolResult Solve(OptionType type, double marketPriceUsd, double f, double k, double t,
        double initialVol = DefaultInitialVol)
    {
        if (double.IsNaN(marketPriceUsd) || double.IsInfinity(marketPriceUsd))
            return Fail("prix invalide");
        if (t <= 0)
            return Fail("maturité nulle");
        if (f <= 0 || k <= 0)
            return Fail("future ou strike invalide");

        // Bornes de non-arbitrage : une solution n'existe que strictement entre les deux.
        double lower = Black76.Intrinsic(type, f, k);
        double upper = type == OptionType.Call ? f : k;
        if (!(marketPriceUsd > lower && marketPriceUsd < upper))
            return Fail($"prix hors des bornes de non-arbitrage ]{lower:F2} ; {upper:F2}[ USD");

        double sigma = initialVol;
        for (int i = 1; i <= MaxNewtonIterations; i++)
        {
            double diff = Black76.PriceUsd(type, f, k, sigma, t) - marketPriceUsd;
            if (Math.Abs(diff) < PriceTolerance)
                return new ImpliedVolResult(sigma, null, i, "Newton");

            double vega = Black76.VegaRaw(f, k, sigma, t);
            if (vega < MinVega) break;                         // vega trop petit : dichotomie

            double next = sigma - diff / vega;
            if (next < MinVol || next > MaxVol) break;         // sigma sort de l'intervalle : dichotomie
            sigma = next;
        }

        return Bisection(type, marketPriceUsd, f, k, t);
    }

    private static ImpliedVolResult Bisection(OptionType type, double target, double f, double k, double t)
    {
        double lo = MinVol, hi = MaxVol;
        double diffLo = Black76.PriceUsd(type, f, k, lo, t) - target;
        double diffHi = Black76.PriceUsd(type, f, k, hi, t) - target;

        if (Math.Abs(diffLo) < PriceTolerance) return new ImpliedVolResult(lo, null, 0, "Dichotomie");
        if (Math.Abs(diffHi) < PriceTolerance) return new ImpliedVolResult(hi, null, 0, "Dichotomie");
        if (diffLo > 0) return Fail("vol implicite inférieure à 0,01 %");
        if (diffHi < 0) return Fail("vol implicite supérieure à 500 %");

        double mid = (lo + hi) / 2;
        for (int i = 1; i <= MaxBisectionIterations; i++)
        {
            mid = (lo + hi) / 2;
            double diff = Black76.PriceUsd(type, f, k, mid, t) - target;
            if (Math.Abs(diff) < PriceTolerance || hi - lo < 1e-14)
                return new ImpliedVolResult(mid, null, i, "Dichotomie");
            if (diff > 0) hi = mid; else lo = mid;
        }
        return new ImpliedVolResult(mid, null, MaxBisectionIterations, "Dichotomie");
    }

    private static ImpliedVolResult Fail(string error) => new(null, error, 0, "");
}
