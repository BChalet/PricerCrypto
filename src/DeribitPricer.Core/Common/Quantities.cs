// Petits outils sur les quantités : est-ce un multiple du pas de cotation, et arrondi au pas.
namespace DeribitPricer.Core.Common;

// Outils sur les quantités : pas de cotation et arrondis.
public static class Quantities
{
    // Vrai si la quantité est un multiple entier du pas (à une tolérance numérique près).
    public static bool IsMultipleOf(double quantity, double step)
    {
        if (step <= 0) return true;
        double ratio = quantity / step;
        return Math.Abs(ratio - Math.Round(ratio)) < 1e-6;
    }

    // Arrondit au multiple du pas le plus proche.
    public static double RoundToStep(double quantity, double step)
    {
        if (step <= 0) return quantity;
        return Math.Round(Math.Round(quantity / step) * step, 10);
    }

    public static bool IsZero(double value) => Math.Abs(value) < 1e-12;
}
