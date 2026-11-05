// La loi normale : N(x) et sa densité n(x), dont Black-76 a besoin.
namespace DeribitPricer.Core.Pricing;

// Loi normale centrée réduite.
public static class NormalDistribution
{
    private const double InvSqrt2Pi = 0.398942280401432677939946059934;

    // Densité n(x).
    public static double Pdf(double x) => InvSqrt2Pi * Math.Exp(-0.5 * x * x);

    // Fonction de répartition N(x), algorithme de Hart (1968) tel que publié par G. West,
    // précis à la double précision (~1e-15) sur tout l'axe réel.
    public static double Cdf(double x)
    {
        double xAbs = Math.Abs(x);
        double c;
        if (xAbs > 37)
        {
            c = 0;
        }
        else
        {
            double e = Math.Exp(-xAbs * xAbs / 2);
            if (xAbs < 7.07106781186547)
            {
                double b = 3.52624965998911E-02 * xAbs + 0.700383064443688;
                b = b * xAbs + 6.37396220353165;
                b = b * xAbs + 33.912866078383;
                b = b * xAbs + 112.079291497871;
                b = b * xAbs + 221.213596169931;
                b = b * xAbs + 220.206867912376;
                c = e * b;
                b = 8.83883476483184E-02 * xAbs + 1.75566716318264;
                b = b * xAbs + 16.064177579207;
                b = b * xAbs + 86.7807322029461;
                b = b * xAbs + 296.564248779674;
                b = b * xAbs + 637.333633378831;
                b = b * xAbs + 793.826512519948;
                b = b * xAbs + 440.413735824752;
                c /= b;
            }
            else
            {
                double b = xAbs + 0.65;
                b = xAbs + 4 / b;
                b = xAbs + 3 / b;
                b = xAbs + 2 / b;
                b = xAbs + 1 / b;
                c = e / b / 2.506628274631;
            }
        }
        return x > 0 ? 1 - c : c;
    }
}
