// Le coeur du sujet (annexes A et B) : la formule de Black-76, le passage USD/BTC et les grecques dans les unités demandées.
namespace DeribitPricer.Core.Pricing;

// Modèle de Black-76 à taux nul (annexe A du sujet).
// F : prix du future de l'échéance, K : strike, sigma : vol (0,55 pour 55 %), T : maturité en années.
// Tous les prix sont en USD ; le prix Deribit (en BTC/ETH) s'obtient en divisant par F.
public static class Black76
{
    public const double SecondsPerYear = 365.0 * 24 * 3600;

    // T = (échéance - horodatage) / (365 x 24 x 3600 s), borné à 0.
    public static double YearFraction(DateTime expiryUtc, DateTime asOfUtc) =>
        Math.Max(0, (expiryUtc - asOfUtc).TotalSeconds) / SecondsPerYear;

    public static (double D1, double D2) D1D2(double f, double k, double sigma, double t)
    {
        double sqrtT = Math.Sqrt(t);
        double d1 = (Math.Log(f / k) + sigma * sigma * t / 2) / (sigma * sqrtT);
        return (d1, d1 - sigma * sqrtT);
    }

    // Valeur intrinsèque (payoff à l'échéance) en USD.
    public static double Intrinsic(OptionType type, double f, double k) =>
        type == OptionType.Call ? Math.Max(f - k, 0) : Math.Max(k - f, 0);

    public static double PriceUsd(OptionType type, double f, double k, double sigma, double t)
    {
        if (t <= 0 || sigma <= 0) return Intrinsic(type, f, k);
        var (d1, d2) = D1D2(f, k, sigma, t);
        return type == OptionType.Call
            ? f * NormalDistribution.Cdf(d1) - k * NormalDistribution.Cdf(d2)
            : k * NormalDistribution.Cdf(-d2) - f * NormalDistribution.Cdf(-d1);
    }

    // Prix exprimé dans la crypto (convention des options inverses Deribit).
    public static double PriceCoin(OptionType type, double f, double k, double sigma, double t) =>
        PriceUsd(type, f, k, sigma, t) / f;

    // Vega « brut » F n(d1) √T (sans division par 100), utilisé par Newton.
    public static double VegaRaw(double f, double k, double sigma, double t)
    {
        if (t <= 0 || sigma <= 0) return 0;
        var (d1, _) = D1D2(f, k, sigma, t);
        return f * NormalDistribution.Pdf(d1) * Math.Sqrt(t);
    }

    // Grecques unitaires d'une option, dans les unités de l'annexe B.
    public static Greeks ComputeGreeks(OptionType type, double f, double k, double sigma, double t)
    {
        if (t <= 0 || sigma <= 0)
        {
            double intrinsicDelta = type == OptionType.Call ? (f > k ? 1 : 0) : (f < k ? -1 : 0);
            return new Greeks(intrinsicDelta, 0, 0, 0);
        }

        double sqrtT = Math.Sqrt(t);
        var (d1, _) = D1D2(f, k, sigma, t);
        double nd1 = NormalDistribution.Pdf(d1);

        double delta = type == OptionType.Call ? NormalDistribution.Cdf(d1) : NormalDistribution.Cdf(d1) - 1;
        double gamma = nd1 / (f * sigma * sqrtT);
        double vega = f * nd1 * sqrtT / 100;                    // USD pour 1 point de vol
        double theta = -(f * nd1 * sigma / (2 * sqrtT)) / 365;  // USD par jour calendaire
        return new Greeks(delta, gamma, vega, theta);
    }
}
