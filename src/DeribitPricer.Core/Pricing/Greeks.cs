// Les quatre grecques ensemble, qu'on peut additionner et multiplier par une quantité pour avoir celles d'une structure ou du book.
namespace DeribitPricer.Core.Pricing;

// Grecques exprimées selon les conventions de l'annexe B :
// delta en unités de sous-jacent, gamma pour 1 USD de sous-jacent,
// vega en USD pour 1 point de vol, theta en USD par jour calendaire.
public readonly record struct Greeks(double Delta, double Gamma, double Vega, double Theta)
{
    public static readonly Greeks Zero = new(0, 0, 0, 0);

    public static Greeks operator +(Greeks a, Greeks b) =>
        new(a.Delta + b.Delta, a.Gamma + b.Gamma, a.Vega + b.Vega, a.Theta + b.Theta);

    public static Greeks operator -(Greeks a, Greeks b) =>
        new(a.Delta - b.Delta, a.Gamma - b.Gamma, a.Vega - b.Vega, a.Theta - b.Theta);

    // Pondération par une quantité signée.
    public static Greeks operator *(double quantity, Greeks g) =>
        new(quantity * g.Delta, quantity * g.Gamma, quantity * g.Vega, quantity * g.Theta);

    public static Greeks Sum(IEnumerable<Greeks> items) => items.Aggregate(Zero, (acc, g) => acc + g);
}
