// À quoi ressemble un trade enregistré : identifiant, heure, structure, sens, quantité, jambes et prime.
using System.Text.Json.Serialization;
using DeribitPricer.Core.Pricing;
using DeribitPricer.Core.Structures;

namespace DeribitPricer.Core.Booking;

public enum LegKind
{
    ListedOption,
    CustomOption,
    Future
}

// Jambe d'un trade telle qu'exécutée.
public sealed class TradeLeg
{
    public string Instrument { get; set; } = "";
    public LegKind Kind { get; set; }
    public OptionType? OptionType { get; set; }
    public double Strike { get; set; }
    public DateTime ExpiryUtc { get; set; }
    public bool IsPerpetual { get; set; }
    // Quantité signée du point de vue du desk (> 0 achat), en unités de sous-jacent.
    public double SignedQuantity { get; set; }
    // Prix d'exécution : en crypto pour une option, en USD pour un future.
    public double ExecutionPrice { get; set; }
    // Future de l'échéance au moment du trade.
    public double Forward { get; set; }
    // Vol au moment du trade (décimal) : mark_iv pour une option cotée, vol interpolée pour une option sur mesure.
    public double Vol { get; set; }
    public double QuantityStep { get; set; }

    [JsonIgnore] public bool IsOption => Kind != LegKind.Future;
}

// Trade enregistré dans le blotter. Un débouclage est un trade lié au trade d'origine.
public sealed class Trade
{
    public string Id { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public string Currency { get; set; } = "BTC";
    public StructureType StructureType { get; set; }
    public string Description { get; set; } = "";
    public TradeDirection Direction { get; set; }
    // Quantité de structure (nombre d'unités).
    public double Quantity { get; set; }
    public List<TradeLeg> Legs { get; set; } = new();
    // Prime totale en crypto, signée : > 0 payée par le desk, < 0 reçue.
    public double PremiumCoin { get; set; }
    // Indice au moment du trade (conversion USD).
    public double IndexAtTrade { get; set; }
    // Trade d'origine si c'est un débouclage.
    public string? ParentTradeId { get; set; }
    // Prix proposé au client pour une option sur mesure (USD par option).
    public double? ClientPriceUsd { get; set; }

    [JsonIgnore] public bool IsUnwind => ParentTradeId != null;
    [JsonIgnore] public string GroupId => ParentTradeId ?? Id;
}
