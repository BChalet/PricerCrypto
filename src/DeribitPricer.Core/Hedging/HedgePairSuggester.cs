// Propose la paire d'options de couverture par défaut : une sur chaque échéance autour de l'option, au strike le plus proche.
using DeribitPricer.Core.Market;
using DeribitPricer.Core.Pricing;

namespace DeribitPricer.Core.Hedging;

public sealed record HedgeSuggestion(Instrument Option1, Instrument Option2, Instrument Future);

// Paire de couverture proposée par défaut : une option sur chacune des deux échéances qui encadrent
// l'option sur mesure (ou l'échéance elle-même et la suivante), de même type, au strike coté le plus
// proche du strike équivalent (même log-moneyness), avec bid et ask. Deux maturités différentes donnent
// deux rapports gamma/vega différents, donc un système bien conditionné.
// Future : l'échéance cotée la plus proche après l'option, sinon le perpétuel.
public static class HedgePairSuggester
{
    public static HedgeSuggestion? Suggest(CustomOptionValuation custom, MarketDataStore store)
    {
        var spec = custom.Spec;
        var expiries = store.GetOptionExpiries(spec.Currency).Where(e => e > store.Now).ToList();
        DateTime e1, e2;
        if (custom.OnListedExpiry)
        {
            e1 = spec.ExpiryUtc;
            int i = expiries.IndexOf(e1);
            if (i < 0) return null;
            if (i + 1 < expiries.Count) e2 = expiries[i + 1];
            else if (i > 0) e2 = expiries[i - 1];
            else return null;
        }
        else
        {
            e1 = custom.Slices[0].Expiry;
            e2 = custom.Slices[1].Expiry;
        }

        var o1 = Nearest(store, spec, e1, custom.LogMoneyness);
        var o2 = Nearest(store, spec, e2, custom.LogMoneyness);
        var futures = store.GetFutures(spec.Currency);
        var future = futures.Where(f => !f.IsPerpetual && f.ExpiryUtc >= spec.ExpiryUtc).OrderBy(f => f.ExpiryUtc).FirstOrDefault()
                     ?? futures.FirstOrDefault(f => f.IsPerpetual)
                     ?? futures.FirstOrDefault();
        return o1 != null && o2 != null && future != null ? new HedgeSuggestion(o1, o2, future) : null;
    }

    private static Instrument? Nearest(MarketDataStore store, CustomOptionSpec spec, DateTime expiry, double k)
    {
        var forward = store.GetForward(spec.Currency, expiry);
        if (forward is not > 0) return null;
        double target = forward.Value * Math.Exp(k);
        return store.GetOptions(spec.Currency, expiry)
            .Where(o => o.OptionType == spec.Type)
            .Where(o => store.GetQuote(o.Name) is { Bid: not null, Ask: not null })
            .OrderBy(o => Math.Abs(o.Strike - target))
            .FirstOrDefault();
    }
}
