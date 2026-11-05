// Les types de structures (spreads, butterflies, sur mesure), le sens achat/vente, et la description d'une structure avec ses jambes.
using DeribitPricer.Core.Market;
using DeribitPricer.Core.Pricing;

namespace DeribitPricer.Core.Structures;

public enum StructureType
{
    CallSpread,
    PutSpread,
    CallButterfly,
    PutButterfly,
    // Call ou put sur mesure (gré à gré), avec sa couverture éventuelle.
    CustomOption
}

public enum TradeDirection
{
    Buy,
    Sell
}

public static class StructureTypeInfo
{
    public static string Label(this StructureType type) => type switch
    {
        StructureType.CallSpread => "Call spread",
        StructureType.PutSpread => "Put spread",
        StructureType.CallButterfly => "Butterfly calls",
        StructureType.PutButterfly => "Butterfly puts",
        StructureType.CustomOption => "Option sur mesure",
        _ => type.ToString()
    };

    public static int StrikeCount(this StructureType type) => type switch
    {
        StructureType.CallSpread or StructureType.PutSpread => 2,
        StructureType.CallButterfly or StructureType.PutButterfly => 3,
        _ => 1
    };

    public static OptionType OptionType(this StructureType type) =>
        type is StructureType.PutSpread or StructureType.PutButterfly ? Pricing.OptionType.Put : Pricing.OptionType.Call;

    // Composition de la structure achetée, pour l'aide à la saisie.
    public static string Composition(this StructureType type) => type switch
    {
        StructureType.CallSpread => "Achat : +1 call K1, −1 call K2 (K1 < K2)",
        StructureType.PutSpread => "Achat : +1 put K2, −1 put K1 (K1 < K2)",
        StructureType.CallButterfly => "Achat : +1 call K1, −2 calls K2, +1 call K3 (K2 − K1 = K3 − K2)",
        StructureType.PutButterfly => "Achat : +1 put K1, −2 puts K2, +1 put K3 (K2 − K1 = K3 − K2)",
        _ => ""
    };

    public static int Sign(this TradeDirection direction) => direction == TradeDirection.Buy ? 1 : -1;

    public static string Label(this TradeDirection direction) => direction == TradeDirection.Buy ? "Achat" : "Vente";

    public static TradeDirection Opposite(this TradeDirection direction) =>
        direction == TradeDirection.Buy ? TradeDirection.Sell : TradeDirection.Buy;
}

// Une jambe : instrument coté et ratio pour une unité de structure achetée (+1, −1, −2...).
public sealed record LegSpec(Instrument Instrument, double Ratio);

// Saisie de l'utilisateur.
public sealed record StructureRequest(
    string Currency,
    StructureType Type,
    DateTime Expiry,
    IReadOnlyList<double> Strikes,
    TradeDirection Direction,
    double Quantity);

// Structure validée, prête à être cotée.
public sealed record StructureDefinition(StructureRequest Request, IReadOnlyList<LegSpec> Legs)
{
    public int DirectionSign => Request.Direction.Sign();

    // Quantité signée d'une jambe, du point de vue du desk, pour la quantité et le sens choisis.
    public double SignedQuantity(LegSpec leg) => leg.Ratio * Request.Quantity * DirectionSign;

    public string Label =>
        $"{Request.Type.Label()} {Instrument.FormatExpiry(Request.Expiry)} {string.Join("/", Request.Strikes.Select(k => k.ToString("0.##")))}";
}
