// Petites fonctions pour afficher les nombres à la française (virgules, %, tiret quand il n'y a pas de valeur).
using System.Globalization;

namespace DeribitPricer.App.Components.Shared;

// Formats d'affichage (culture française, « — » pour une valeur absente).
public static class Fmt
{
    public static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private const string Missing = "—";

    public static string Coin(double? v, int decimals = 4) => v.HasValue ? v.Value.ToString("N" + decimals, Fr) : Missing;
    public static string Usd(double? v, int decimals = 2) => v.HasValue ? v.Value.ToString("N" + decimals, Fr) : Missing;
    public static string Num(double? v, string format) => v.HasValue ? v.Value.ToString(format, Fr) : Missing;
    public static string Signed(double? v, int decimals = 4) =>
        v.HasValue ? (v.Value > 0 ? "+" : "") + v.Value.ToString("N" + decimals, Fr) : Missing;
    // Vol exprimée en % (55,30).
    public static string Iv(double? pct) => pct.HasValue ? pct.Value.ToString("0.00", Fr) : Missing;
    public static string Qty(double? v) => v.HasValue ? v.Value.ToString("0.####", Fr) : Missing;
    public static string Strike(double v) => v.ToString("N0", Fr);
    public static string Pct(double? v, int decimals = 2) => v.HasValue ? v.Value.ToString("N" + decimals, Fr) + " %" : Missing;
    public static string DateTime(System.DateTime d) => d.ToString("dd/MM/yyyy HH:mm:ss", Fr);
    public static string Time(System.DateTime d) => d.ToString("HH:mm:ss", Fr);
    public static string Date(System.DateTime d) => d.ToString("dd/MM/yyyy", Fr);
    public static string Days(double years) => (years * 365).ToString("0.00", Fr) + " j";

    // Gamma : très petit en USD, affiché en notation scientifique.
    public static string Gamma(double? v) => v.HasValue ? v.Value.ToString("0.000E+0", Fr) : Missing;

    public static string SignClass(double? v) => v switch { > 1e-12 => "pos", < -1e-12 => "neg", _ => "" };
}
