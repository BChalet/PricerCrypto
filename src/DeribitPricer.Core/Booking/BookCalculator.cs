// Le calcul du book : on rejoue les trades, on valorise les positions, on règle les options échues et on agrège P&L et grecques.
using DeribitPricer.Core.Market;
using DeribitPricer.Core.Pricing;

namespace DeribitPricer.Core.Booking;

public enum LegStatus
{
    // Position ouverte.
    Open,
    // Échue, prix de règlement Deribit pas encore publié : valorisée au payoff sur l'indice.
    PendingSettlement,
    // Réglée au payoff avec le prix de règlement Deribit : sortie du book, montant dans le réalisé.
    Settled,
    // Entièrement débouclée.
    Closed
}

// Position nette sur un instrument au sein d'une structure.
public sealed record BookLegView
{
    public required string GroupId { get; init; }
    public required string Instrument { get; init; }
    public LegKind Kind { get; init; }
    public string Currency { get; init; } = "";
    public DateTime Expiry { get; init; }
    public bool IsPerpetual { get; init; }
    public double OpenQuantity { get; init; }
    // Prix de revient moyen (crypto pour une option, USD pour un future).
    public double AverageCost { get; init; }
    public double? MarkPrice { get; init; }
    public double? TheoPrice { get; init; }
    public double MarkValueCoin { get; init; }
    public double TheoValueCoin { get; init; }
    public double RealizedCoin { get; init; }
    public double UnrealizedMarkCoin { get; init; }
    public double UnrealizedTheoCoin { get; init; }
    public Greeks Greeks { get; init; }
    public LegStatus Status { get; init; }
    public double? SettlementPrice { get; init; }
    public string? Note { get; init; }
}

public sealed record BookStructureView
{
    public required Trade Original { get; init; }
    public double OpenQuantity { get; init; }
    public required IReadOnlyList<BookLegView> Legs { get; init; }
    public double MarkValueCoin { get; init; }
    public double TheoValueCoin { get; init; }
    public double RealizedCoin { get; init; }
    // P&L depuis l'ouverture = réalisé + latent (au mark).
    public double PnlMarkCoin { get; init; }
    public double PnlTheoCoin { get; init; }
    public double PnlMarkUsd { get; init; }
    public double PnlTheoUsd { get; init; }
    public Greeks Greeks { get; init; }
    public bool IsOpen { get; init; }
}

public sealed record ExpiryGreeks(string Currency, DateTime Expiry, bool IsPerpetual, Greeks Greeks);

public sealed record CurrencyTotals
{
    public required string Currency { get; init; }
    public double Index { get; init; }
    public double MarkValueCoin { get; init; }
    public double TheoValueCoin { get; init; }
    public double PnlMarkCoin { get; init; }
    public double PnlTheoCoin { get; init; }
    public double RealizedCoin { get; init; }
    public double UnrealizedMarkCoin => PnlMarkCoin - RealizedCoin;
    public double PnlMarkUsd => PnlMarkCoin * Index;
    public double PnlTheoUsd => PnlTheoCoin * Index;
    public double RealizedUsd => RealizedCoin * Index;
    public Greeks Greeks { get; init; }
}

public sealed record BookSnapshot(
    DateTime AsOf,
    IReadOnlyList<BookStructureView> Structures,
    IReadOnlyList<ExpiryGreeks> GreeksByExpiry,
    IReadOnlyList<CurrencyTotals> Totals,
    IReadOnlyList<BookLegView> SettledLegs);

// Agrège les trades en positions et les valorise au marché courant.
// Chaque instrument d'une structure est suivi au prix de revient moyen : réduire une position
// (débouclage, règlement à l'échéance) réalise le P&L correspondant.
public static class BookCalculator
{
    private static readonly System.Globalization.CultureInfo Fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    private sealed class Ledger
    {
        public required TradeLeg Template { get; init; }
        public double Position;
        public double AverageCost;
        public double Realized;  // en unité native (crypto pour une option, USD pour un future)

        public void Apply(double quantity, double price)
        {
            if (Math.Abs(Position) < 1e-12 || Math.Sign(Position) == Math.Sign(quantity))
            {
                double newPosition = Position + quantity;
                AverageCost = (AverageCost * Position + price * quantity) / newPosition;
                Position = newPosition;
                return;
            }
            double closed = Math.Min(Math.Abs(quantity), Math.Abs(Position));
            Realized += (price - AverageCost) * closed * Math.Sign(Position);
            double remaining = Position + quantity;
            if (Math.Abs(remaining) < 1e-10) { Position = 0; return; }
            if (Math.Sign(remaining) != Math.Sign(Position)) AverageCost = price;  // la position change de signe
            Position = remaining;
        }
    }

    public static BookSnapshot Compute(IReadOnlyList<Trade> trades, MarketDataStore store)
    {
        var now = store.Now;
        var structures = new List<BookStructureView>();
        var customCache = new Dictionary<string, CustomOptionValuation?>();

        var groups = trades.GroupBy(t => t.GroupId);
        foreach (var group in groups)
        {
            var original = group.FirstOrDefault(t => !t.IsUnwind);
            if (original == null) continue;
            string currency = original.Currency;
            double index = store.GetIndex(currency) ?? original.IndexAtTrade;

            // 1. Rejoue les exécutions dans l'ordre.
            var ledgers = new Dictionary<string, Ledger>();
            foreach (var trade in group.OrderBy(t => t.TimestampUtc).ThenBy(t => t.Id))
            foreach (var leg in trade.Legs)
            {
                if (!ledgers.TryGetValue(leg.Instrument, out var ledger))
                    ledgers[leg.Instrument] = ledger = new Ledger { Template = leg };
                ledger.Apply(leg.SignedQuantity, leg.ExecutionPrice);
            }

            // 2. Règlement des échus et valorisation des positions ouvertes.
            var legViews = new List<BookLegView>();
            foreach (var ledger in ledgers.Values)
                legViews.Add(ValueLeg(ledger, original, group.Key, index, now, store, customCache));

            double realized = legViews.Sum(l => l.RealizedCoin);
            double pnlMark = realized + legViews.Sum(l => l.UnrealizedMarkCoin);
            double pnlTheo = realized + legViews.Sum(l => l.UnrealizedTheoCoin);
            double unwound = group.Where(t => t.IsUnwind).Sum(t => t.Quantity);
            bool open = legViews.Any(l => l.Status is LegStatus.Open or LegStatus.PendingSettlement);

            structures.Add(new BookStructureView
            {
                Original = original,
                OpenQuantity = open ? Math.Round(original.Quantity - unwound, 10) : 0,
                Legs = legViews,
                MarkValueCoin = legViews.Sum(l => l.MarkValueCoin),
                TheoValueCoin = legViews.Sum(l => l.TheoValueCoin),
                RealizedCoin = realized,
                PnlMarkCoin = pnlMark,
                PnlTheoCoin = pnlTheo,
                PnlMarkUsd = pnlMark * index,
                PnlTheoUsd = pnlTheo * index,
                Greeks = Greeks.Sum(legViews.Select(l => l.Greeks)),
                IsOpen = open
            });
        }

        var openLegs = structures.SelectMany(s => s.Legs).Where(l => l.Status is LegStatus.Open or LegStatus.PendingSettlement).ToList();
        var byExpiry = openLegs
            .GroupBy(l => (l.Currency, l.Expiry, l.IsPerpetual))
            .Select(g => new ExpiryGreeks(g.Key.Currency, g.Key.Expiry, g.Key.IsPerpetual, Greeks.Sum(g.Select(l => l.Greeks))))
            .OrderBy(e => e.Currency).ThenBy(e => e.IsPerpetual).ThenBy(e => e.Expiry)
            .ToList();

        var totals = structures
            .GroupBy(s => s.Original.Currency)
            .Select(g => new CurrencyTotals
            {
                Currency = g.Key,
                Index = store.GetIndex(g.Key) ?? g.First().Original.IndexAtTrade,
                MarkValueCoin = g.Sum(s => s.MarkValueCoin),
                TheoValueCoin = g.Sum(s => s.TheoValueCoin),
                PnlMarkCoin = g.Sum(s => s.PnlMarkCoin),
                PnlTheoCoin = g.Sum(s => s.PnlTheoCoin),
                RealizedCoin = g.Sum(s => s.RealizedCoin),
                Greeks = Greeks.Sum(g.Select(s => s.Greeks))
            })
            .OrderBy(t => t.Currency)
            .ToList();

        var settled = structures.SelectMany(s => s.Legs).Where(l => l.Status == LegStatus.Settled).ToList();
        return new BookSnapshot(now, structures, byExpiry, totals, settled);
    }

    private static BookLegView ValueLeg(Ledger ledger, Trade original, string groupId, double index, DateTime now,
        MarketDataStore store, Dictionary<string, CustomOptionValuation?> customCache)
    {
        var leg = ledger.Template;
        string currency = original.Currency;
        bool isFuture = leg.Kind == LegKind.Future;
        double toCoin = isFuture ? 1 / index : 1;  // les futures sont suivis en USD

        var view = new BookLegView
        {
            GroupId = groupId,
            Instrument = leg.Instrument,
            Kind = leg.Kind,
            Currency = currency,
            Expiry = leg.ExpiryUtc,
            IsPerpetual = leg.IsPerpetual,
            AverageCost = ledger.AverageCost
        };

        bool expired = !leg.IsPerpetual && leg.ExpiryUtc <= now;
        if (Math.Abs(ledger.Position) < 1e-12)
            return view with { Status = expired ? LegStatus.Settled : LegStatus.Closed, RealizedCoin = ledger.Realized * toCoin };

        if (expired)
        {
            // Échu : règlement au payoff avec le prix de règlement Deribit de la date d'échéance.
            var delivery = store.GetDeliveryPrice(currency, DateOnly.FromDateTime(leg.ExpiryUtc));
            if (delivery.HasValue)
            {
                double settle = SettlementValue(leg, delivery.Value);
                double qty = ledger.Position;
                ledger.Apply(-qty, settle);
                return view with
                {
                    Status = LegStatus.Settled,
                    SettlementPrice = delivery.Value,
                    MarkPrice = settle,
                    TheoPrice = settle,
                    RealizedCoin = ledger.Realized * toCoin,
                    Note = string.Create(Fr, $"Réglé à {delivery.Value:N2} USD ({qty:0.####} @ {settle:0.######})")
                };
            }

            double estimate = store.GetIndex(currency) ?? leg.Forward;
            double value = SettlementValue(leg, estimate);
            double unrealized = (value - ledger.AverageCost) * ledger.Position * toCoin;
            return view with
            {
                Status = LegStatus.PendingSettlement,
                OpenQuantity = ledger.Position,
                MarkPrice = value,
                TheoPrice = value,
                MarkValueCoin = isFuture ? unrealized : value * ledger.Position,
                TheoValueCoin = isFuture ? unrealized : value * ledger.Position,
                RealizedCoin = ledger.Realized * toCoin,
                UnrealizedMarkCoin = unrealized,
                UnrealizedTheoCoin = unrealized,
                Note = "En attente du prix de règlement Deribit (estimé sur l'indice)"
            };
        }

        // Position ouverte : prix de marché, prix théorique et grecques.
        double? mark, theo;
        Greeks unitGreeks;
        string? note = null;
        switch (leg.Kind)
        {
            case LegKind.Future:
            {
                var q = store.GetQuote(leg.Instrument);
                mark = theo = q?.Mark ?? q?.Mid ?? leg.ExecutionPrice;
                unitGreeks = new Greeks(1, 0, 0, 0);
                if (q == null) note = "Pas de cotation : valorisé au prix d'exécution";
                break;
            }
            case LegKind.CustomOption:
            {
                var spec = new CustomOptionSpec(currency, leg.OptionType!.Value, leg.Strike, leg.ExpiryUtc);
                if (!customCache.TryGetValue(spec.Name, out var valuation))
                {
                    var result = CustomOptionPricer.Value(spec, store);
                    customCache[spec.Name] = valuation = result.IsSuccess ? result.Value : null;
                }
                if (valuation != null)
                {
                    mark = theo = valuation.TheoCoin;
                    unitGreeks = valuation.UnitGreeks;
                }
                else
                {
                    (theo, unitGreeks) = ModelValue(leg, currency, store, now);
                    mark = theo;
                    note = "Interpolation impossible : Black-76 à la vol du trade";
                }
                note ??= "Non cotée : mark = prix théorique interpolé";
                break;
            }
            default:
            {
                var instrument = store.GetInstrument(leg.Instrument);
                var analytics = instrument != null ? OptionAnalytics.Compute(instrument, store.GetQuote(leg.Instrument)) : null;
                if (analytics?.Greeks != null)
                {
                    theo = analytics.TheoCoin;
                    mark = analytics.Quote.Mark ?? theo;
                    unitGreeks = analytics.Greeks.Value;
                }
                else
                {
                    (theo, unitGreeks) = ModelValue(leg, currency, store, now);
                    mark = theo;
                    note = "Pas de cotation : Black-76 à la vol du trade";
                }
                break;
            }
        }

        double p = ledger.Position;
        double unrealizedMark = (mark!.Value - ledger.AverageCost) * p * toCoin;
        double unrealizedTheo = (theo!.Value - ledger.AverageCost) * p * toCoin;
        return view with
        {
            Status = LegStatus.Open,
            OpenQuantity = p,
            MarkPrice = mark,
            TheoPrice = theo,
            MarkValueCoin = isFuture ? unrealizedMark : mark.Value * p,
            TheoValueCoin = isFuture ? unrealizedTheo : theo.Value * p,
            RealizedCoin = ledger.Realized * toCoin,
            UnrealizedMarkCoin = unrealizedMark,
            UnrealizedTheoCoin = unrealizedTheo,
            Greeks = p * unitGreeks,
            Note = note
        };
    }

    // Payoff à l'échéance : en crypto pour une option (payoff USD / prix de règlement), en USD pour un future.
    public static double SettlementValue(TradeLeg leg, double settlementPrice) =>
        leg.Kind == LegKind.Future
            ? settlementPrice
            : Black76.Intrinsic(leg.OptionType!.Value, settlementPrice, leg.Strike) / settlementPrice;

    // Repli sans cotation : Black-76 à la vol du trade, sur le future de l'échéance (ou l'indice).
    private static (double Theo, Greeks Greeks) ModelValue(TradeLeg leg, string currency, MarketDataStore store, DateTime now)
    {
        double t = Black76.YearFraction(leg.ExpiryUtc, now);
        double f = store.GetForward(currency, leg.ExpiryUtc) ?? leg.Forward;
        if (f <= 0) f = leg.Forward;
        var type = leg.OptionType ?? OptionType.Call;
        return (Black76.PriceCoin(type, f, leg.Strike, leg.Vol, t), Black76.ComputeGreeks(type, f, leg.Strike, leg.Vol, t));
    }
}
