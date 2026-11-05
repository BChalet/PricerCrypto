// Fabrique les trades au prix du moment : une structure, une option sur mesure avec sa couverture, ou un débouclage.
using System.Globalization;
using DeribitPricer.Core.Common;
using DeribitPricer.Core.Hedging;
using DeribitPricer.Core.Market;
using DeribitPricer.Core.Pricing;
using DeribitPricer.Core.Structures;

namespace DeribitPricer.Core.Booking;

// Construit les trades à partir des cotations affichées (prix exécutables du moment).
public static class TradeFactory
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    // Trade d'une structure cotée, au prix exécutable affiché.
    public static BuildResult<Trade> FromStructure(StructurePricing pricing, MarketDataStore store)
    {
        if (!pricing.Tradable)
            return BuildResult<Trade>.Fail("Structure non traitable : " + string.Join(", ", pricing.NotTradableReasons));

        var def = pricing.Definition;
        var legs = new List<TradeLeg>();
        foreach (var leg in pricing.Legs)
        {
            var a = leg.Analytics!;
            double price = leg.SignedQuantity > 0 ? a.Quote.Ask!.Value : a.Quote.Bid!.Value;
            legs.Add(ListedLeg(a, leg.SignedQuantity, price));
        }

        return BuildResult<Trade>.Ok(new Trade
        {
            TimestampUtc = store.Now,
            Currency = def.Request.Currency,
            StructureType = def.Request.Type,
            Description = def.Label,
            Direction = def.Request.Direction,
            Quantity = def.Request.Quantity,
            Legs = legs,
            PremiumCoin = legs.Sum(l => l.SignedQuantity * l.ExecutionPrice),
            IndexAtTrade = store.GetIndex(def.Request.Currency) ?? pricing.Forward
        });
    }

    // Trade d'une option sur mesure au prix client, avec sa couverture sur Deribit.
    public static Trade FromCustom(HedgeResult? hedge, CustomOptionValuation custom, double q0, double clientPriceUsd, MarketDataStore store)
    {
        var spec = custom.Spec;
        var legs = new List<TradeLeg>
        {
            new()
            {
                Instrument = spec.Name,
                Kind = LegKind.CustomOption,
                OptionType = spec.Type,
                Strike = spec.Strike,
                ExpiryUtc = spec.ExpiryUtc,
                SignedQuantity = q0,
                ExecutionPrice = clientPriceUsd / custom.Forward,
                Forward = custom.Forward,
                Vol = custom.Vol,
                QuantityStep = 0.1
            }
        };

        if (hedge != null)
        {
            foreach (var line in hedge.Lines.Where(l => !Quantities.IsZero(l.Quantity)))
            {
                legs.Add(new TradeLeg
                {
                    Instrument = line.Instrument.Name,
                    Kind = line.IsFuture ? LegKind.Future : LegKind.ListedOption,
                    OptionType = line.Instrument.OptionType,
                    Strike = line.Instrument.Strike,
                    ExpiryUtc = line.Instrument.ExpiryUtc,
                    IsPerpetual = line.Instrument.IsPerpetual,
                    SignedQuantity = line.Quantity,
                    ExecutionPrice = line.ExecPrice,
                    Forward = line.Forward,
                    Vol = line.Vol,
                    QuantityStep = line.QuantityStep
                });
            }
        }

        var typeLabel = spec.Type == OptionType.Call ? "Call" : "Put";
        return new Trade
        {
            TimestampUtc = store.Now,
            Currency = spec.Currency,
            StructureType = StructureType.CustomOption,
            Description = $"{typeLabel} sur mesure {Instrument.FormatExpiry(spec.ExpiryUtc)} {spec.Strike.ToString("0.##", Fr)}"
                          + (hedge != null ? " + couverture" : ""),
            // Sens vu du desk : le client achète => le desk vend.
            Direction = q0 > 0 ? TradeDirection.Buy : TradeDirection.Sell,
            Quantity = Math.Abs(q0),
            Legs = legs,
            PremiumCoin = legs.Where(l => l.IsOption).Sum(l => l.SignedQuantity * l.ExecutionPrice),
            IndexAtTrade = store.GetIndex(spec.Currency) ?? custom.Forward,
            ClientPriceUsd = clientPriceUsd
        };
    }

    // Débouclage de tout ou partie d'un trade : chaque jambe est retournée au prorata,
    // au prix exécutable du moment (ask pour racheter, bid pour revendre ; prix théorique pour une option sur mesure).
    public static BuildResult<Trade> Unwind(Trade original, double quantity, double openQuantity, MarketDataStore store)
    {
        if (original.IsUnwind) return BuildResult<Trade>.Fail("On déboucle le trade d'origine, pas un débouclage.");
        double step = original.Legs.Count > 0 ? original.Legs[0].QuantityStep : 0.1;
        if (quantity <= 0) return BuildResult<Trade>.Fail("La quantité à déboucler doit être strictement positive.");
        if (quantity > openQuantity + 1e-9)
            return BuildResult<Trade>.Fail($"Quantité supérieure à la quantité ouverte ({openQuantity.ToString("0.####", Fr)}).");
        if (!Quantities.IsMultipleOf(quantity, step))
            return BuildResult<Trade>.Fail($"La quantité doit être un multiple de {step.ToString("0.####", Fr)}.");

        var now = store.Now;
        double fraction = quantity / original.Quantity;
        var errors = new List<string>();
        var legs = new List<TradeLeg>();

        foreach (var leg in original.Legs)
        {
            if (!leg.IsPerpetual && leg.ExpiryUtc <= now)
            {
                errors.Add($"{leg.Instrument} est échue : débouclage impossible.");
                continue;
            }
            double qty = Quantities.RoundToStep(-leg.SignedQuantity * fraction, leg.QuantityStep);
            if (Quantities.IsZero(qty)) continue;

            var unwindLeg = new TradeLeg
            {
                Instrument = leg.Instrument,
                Kind = leg.Kind,
                OptionType = leg.OptionType,
                Strike = leg.Strike,
                ExpiryUtc = leg.ExpiryUtc,
                IsPerpetual = leg.IsPerpetual,
                SignedQuantity = qty,
                QuantityStep = leg.QuantityStep
            };

            if (leg.Kind == LegKind.CustomOption)
            {
                var spec = new CustomOptionSpec(original.Currency, leg.OptionType!.Value, leg.Strike, leg.ExpiryUtc);
                var value = CustomOptionPricer.Value(spec, store);
                if (!value.IsSuccess)
                {
                    errors.Add($"{leg.Instrument} : valorisation impossible ({string.Join(" ", value.Errors)}).");
                    continue;
                }
                unwindLeg.ExecutionPrice = value.Value!.TheoCoin;
                unwindLeg.Forward = value.Value.Forward;
                unwindLeg.Vol = value.Value.Vol;
            }
            else
            {
                var quote = store.GetQuote(leg.Instrument);
                double? price = qty > 0 ? quote?.Ask : quote?.Bid;
                if (!price.HasValue)
                {
                    errors.Add($"{leg.Instrument} : pas de {(qty > 0 ? "ask" : "bid")}, débouclage non traitable.");
                    continue;
                }
                unwindLeg.ExecutionPrice = price.Value;
                unwindLeg.Forward = leg.Kind == LegKind.Future ? price.Value : quote!.UnderlyingPrice;
                unwindLeg.Vol = (quote!.MarkIv ?? 0) / 100;
            }
            legs.Add(unwindLeg);
        }

        if (errors.Count > 0) return BuildResult<Trade>.Fail(errors);

        return BuildResult<Trade>.Ok(new Trade
        {
            TimestampUtc = now,
            Currency = original.Currency,
            StructureType = original.StructureType,
            Description = $"Débouclage {original.Id} : {original.Description}",
            Direction = original.Direction.Opposite(),
            Quantity = quantity,
            Legs = legs,
            PremiumCoin = legs.Where(l => l.IsOption).Sum(l => l.SignedQuantity * l.ExecutionPrice),
            IndexAtTrade = store.GetIndex(original.Currency) ?? 0,
            ParentTradeId = original.Id
        });
    }

    private static TradeLeg ListedLeg(OptionAnalytics a, double signedQuantity, double price) => new()
    {
        Instrument = a.Instrument.Name,
        Kind = LegKind.ListedOption,
        OptionType = a.Instrument.OptionType,
        Strike = a.Instrument.Strike,
        ExpiryUtc = a.Instrument.ExpiryUtc,
        SignedQuantity = signedQuantity,
        ExecutionPrice = price,
        Forward = a.Forward,
        Vol = a.Vol ?? 0,
        QuantityStep = a.Instrument.MinTradeAmount
    };
}
