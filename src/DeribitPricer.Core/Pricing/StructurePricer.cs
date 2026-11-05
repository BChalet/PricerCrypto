// Cote une structure entière : somme des jambes, prix exécutable à l'achat et à la vente, fourchette, non traitable, grecques.
using DeribitPricer.Core.Market;
using DeribitPricer.Core.Structures;

namespace DeribitPricer.Core.Pricing;

// Une jambe cotée : quantité signée (desk) et analyse de marché (null si pas de cotation).
public sealed record LegPricing(LegSpec Leg, double SignedQuantity, OptionAnalytics? Analytics)
{
    public string Name => Leg.Instrument.Name;
    public MarketQuote? Quote => Analytics?.Quote;
}

// Cotation complète d'une structure. Les prix « unitaires » sont pour une unité de structure
// achetée (somme des ratios x prix des jambes), en crypto ; les totaux tiennent compte
// de la quantité et du sens.
public sealed record StructurePricing
{
    public required StructureDefinition Definition { get; init; }
    public required IReadOnlyList<LegPricing> Legs { get; init; }
    public bool Complete => Legs.All(l => l.Analytics != null);
    public double Forward { get; init; }
    public double T { get; init; }
    public DateTime QuoteTime { get; init; }

    public double? TheoCoinUnit { get; init; }
    public double? TheoUsdUnit { get; init; }
    public double? MarkCoinUnit { get; init; }

    // Prix payé pour acheter une unité : ask des jambes achetées, bid des jambes vendues.
    public double? BuyPriceCoinUnit { get; init; }
    // Prix reçu pour vendre une unité : bid des jambes achetées, ask des jambes vendues.
    public double? SellPriceCoinUnit { get; init; }
    public IReadOnlyList<string> NotTradableReasons { get; init; } = Array.Empty<string>();
    public bool Tradable => BuyPriceCoinUnit.HasValue && SellPriceCoinUnit.HasValue && NotTradableReasons.Count == 0;
    public double? SpreadCoinUnit => BuyPriceCoinUnit - SellPriceCoinUnit;

    // Prix exécutable unitaire dans le sens choisi.
    public double? ExecPriceCoinUnit => Definition.Request.Direction == TradeDirection.Buy ? BuyPriceCoinUnit : SellPriceCoinUnit;
    // Prime totale signée en crypto (> 0 : payée par le desk).
    public double? PremiumCoin => ExecPriceCoinUnit * Definition.Request.Quantity * Definition.DirectionSign;

    // Grecques de la position (somme des grecques des jambes x quantités signées).
    public Greeks? Greeks { get; init; }
    public Greeks? DeribitGreeks { get; init; }
}

public static class StructurePricer
{
    public static StructurePricing Price(StructureDefinition definition, MarketDataStore store)
    {
        var legs = definition.Legs
            .Select(leg => new LegPricing(leg, definition.SignedQuantity(leg),
                OptionAnalytics.Compute(leg.Instrument, store.GetQuote(leg.Instrument.Name))))
            .ToList();

        var reasons = new List<string>();
        double? theoCoin = 0, theoUsd = 0, mark = 0, buy = 0, sell = 0;
        Greeks? greeks = Pricing.Greeks.Zero, deribitGreeks = Pricing.Greeks.Zero;

        foreach (var leg in legs)
        {
            double ratio = leg.Leg.Ratio;
            var a = leg.Analytics;
            if (a == null)
            {
                reasons.Add($"{leg.Name} : pas de cotation");
                theoCoin = theoUsd = mark = buy = sell = null;
                greeks = deribitGreeks = null;
                continue;
            }

            theoCoin += ratio * a.TheoCoin;
            theoUsd += ratio * a.TheoUsd;
            mark += ratio * a.Quote.Mark;

            // Achat de la structure : on paie l'ask des jambes achetées, on reçoit le bid des jambes vendues.
            double? payOnBuy = ratio > 0 ? a.Quote.Ask : a.Quote.Bid;
            double? receiveOnSell = ratio > 0 ? a.Quote.Bid : a.Quote.Ask;
            if (!a.Quote.Bid.HasValue) reasons.Add($"{leg.Name} : pas de bid");
            if (!a.Quote.Ask.HasValue) reasons.Add($"{leg.Name} : pas d'ask");
            buy += ratio * payOnBuy;
            sell += ratio * receiveOnSell;

            greeks = a.Greeks.HasValue ? greeks + leg.SignedQuantity * a.Greeks.Value : null;
            deribitGreeks = a.DeribitGreeks.HasValue ? deribitGreeks + leg.SignedQuantity * a.DeribitGreeks.Value : null;
        }

        var priced = legs.Where(l => l.Analytics != null).Select(l => l.Analytics!).ToList();
        return new StructurePricing
        {
            Definition = definition,
            Legs = legs,
            Forward = priced.Count > 0 ? priced.Average(a => a.Forward) : 0,
            T = priced.Count > 0 ? priced.Min(a => a.T) : 0,
            QuoteTime = priced.Count > 0 ? priced.Max(a => a.Quote.TimestampUtc) : default,
            TheoCoinUnit = theoCoin,
            TheoUsdUnit = theoUsd,
            MarkCoinUnit = mark,
            BuyPriceCoinUnit = buy,
            SellPriceCoinUnit = sell,
            NotTradableReasons = reasons,
            Greeks = greeks,
            DeribitGreeks = deribitGreeks
        };
    }
}
