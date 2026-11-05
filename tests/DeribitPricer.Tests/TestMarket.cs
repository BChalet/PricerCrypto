// Un faux marché fabriqué pour les tests : quelques échéances et strikes, avec des prix cohérents.
using DeribitPricer.Core.Market;
using DeribitPricer.Core.Pricing;

namespace DeribitPricer.Tests;

// Construit un marché synthétique cohérent (smile simple, bid/ask autour du mark) pour les tests.
public static class TestMarket
{
    public static readonly DateTime Now = new(2026, 10, 14, 7, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime E1 = new(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime E2 = new(2026, 11, 27, 8, 0, 0, DateTimeKind.Utc);
    public const double Spot = 100_000;

    public static double Forward(DateTime expiry) => Spot * (1 + 0.05 * Black76.YearFraction(expiry, Now));
    public static double Vol(DateTime expiry, double k) => 0.50 - 0.10 * Math.Log(k / Forward(expiry)) + 0.3 * Math.Pow(Math.Log(k / Forward(expiry)), 2);

    public static MarketDataStore Create()
    {
        var store = new MarketDataStore { UseWallClock = false };
        store.AdvanceClock(Now);
        store.SetIndex("BTC", Spot);
        foreach (var expiry in new[] { E1, E2 })
        {
            for (double k = 80_000; k <= 120_000; k += 2_000)
            {
                foreach (var type in new[] { OptionType.Call, OptionType.Put })
                {
                    var instrument = new Instrument
                    {
                        Name = $"BTC-{Instrument.FormatExpiry(expiry)}-{k:0}-{(type == OptionType.Call ? "C" : "P")}",
                        Currency = "BTC", Kind = InstrumentKind.Option, OptionType = type, Strike = k,
                        ExpiryUtc = expiry, MinTradeAmount = 0.1, TickSize = 0.0001
                    };
                    store.UpsertInstrument(instrument);
                    store.SetQuote(Quote(instrument));
                }
            }
            var future = new Instrument
            {
                Name = $"BTC-{Instrument.FormatExpiry(expiry)}", Currency = "BTC", Kind = InstrumentKind.Future,
                ExpiryUtc = expiry, ContractSize = 10, MinTradeAmount = 10
            };
            store.UpsertInstrument(future);
            double f = Forward(expiry);
            store.SetQuote(new MarketQuote { InstrumentName = future.Name, TimestampUtc = Now, Bid = f - 2.5, Ask = f + 2.5, Mark = f, UnderlyingPrice = f });
        }
        return store;
    }

    public static MarketQuote Quote(Instrument o)
    {
        double f = Forward(o.ExpiryUtc), t = Black76.YearFraction(o.ExpiryUtc, Now), vol = Vol(o.ExpiryUtc, o.Strike);
        double mark = Black76.PriceCoin(o.OptionType!.Value, f, o.Strike, vol, t);
        return new MarketQuote
        {
            InstrumentName = o.Name, TimestampUtc = Now, Mark = mark, MarkIv = vol * 100,
            Bid = Math.Max(0.0001, mark - 0.0005), Ask = mark + 0.0005, UnderlyingPrice = f, IndexPrice = Spot,
            Source = QuoteSource.Summary
        };
    }
}
