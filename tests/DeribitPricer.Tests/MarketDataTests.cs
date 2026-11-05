// Tests des données : lecture des messages Deribit, enregistrement puis rejeu, limiteur de fréquence.
using System.Text.Json;
using DeribitPricer.Core.Feeds;
using DeribitPricer.Core.Market;

namespace DeribitPricer.Tests;

[TestClass]
public class MarketDataTests
{
    private static MarketEvent Event(string src, string name, object? parameters, string json, long ts = 1_760_000_000_000) =>
        new(ts, src, name, parameters == null ? null : JsonSerializer.SerializeToElement(parameters), JsonDocument.Parse(json).RootElement.Clone());

    [TestMethod]
    public void Parser_ReadsInstrumentsTickerAndDelivery()
    {
        var store = new MarketDataStore { UseWallClock = false };
        DeribitMessageParser.Apply(Event("rest", "public/get_instruments", new { currency = "BTC", kind = "option" },
            """[{"instrument_name":"BTC-27NOV26-100000-C","base_currency":"BTC","kind":"option","option_type":"call","strike":100000,"expiration_timestamp":1795766400000,"contract_size":1,"min_trade_amount":0.1,"tick_size":0.0001,"is_active":true}]"""), store);
        DeribitMessageParser.Apply(Event("ws", "ticker.BTC-27NOV26-100000-C.100ms", null,
            """{"instrument_name":"BTC-27NOV26-100000-C","timestamp":1760000000000,"best_bid_price":0.05,"best_bid_amount":3,"best_ask_price":0,"best_ask_amount":0,"mark_price":0.051,"mark_iv":48.2,"bid_iv":47.9,"ask_iv":0,"underlying_price":101000,"index_price":100500,"greeks":{"delta":0.55,"gamma":0.00001,"vega":120.5,"theta":-80.1}}"""), store);
        DeribitMessageParser.Apply(Event("rest", "public/get_delivery_prices", new { index_name = "btc_usd" },
            """{"data":[{"date":"2026-10-13","delivery_price":99876.5}],"records_total":1}"""), store);

        var instrument = store.GetInstrument("BTC-27NOV26-100000-C")!;
        Assert.AreEqual(100_000, instrument.Strike);
        Assert.AreEqual("27NOV26", instrument.ExpiryCode);
        var q = store.GetQuote(instrument.Name)!;
        Assert.AreEqual(0.05, q.Bid);
        Assert.IsNull(q.Ask);       // pas d'ask : prix 0 et quantité 0
        Assert.IsNull(q.AskIv);
        Assert.AreEqual(0.55, q.DeribitGreeks!.Value.Delta);
        Assert.AreEqual(101_000, store.GetForward("BTC", instrument.ExpiryUtc));
        Assert.AreEqual(100_500, store.GetIndex("BTC"));
        Assert.AreEqual(99_876.5, store.GetDeliveryPrice("BTC", new DateOnly(2026, 10, 13)));
        Assert.AreEqual(DeribitMessageParser.FromUnixMs(1_760_000_000_000), store.Now);
    }

    [TestMethod]
    public void Parser_KeepsRecentTickerOverSummary()
    {
        var store = new MarketDataStore();
        DeribitMessageParser.Apply(Event("ws", "ticker.X.100ms", null,
            """{"instrument_name":"X","timestamp":1760000000000,"best_bid_price":0.05,"best_bid_amount":1,"best_ask_price":0.06,"best_ask_amount":1,"mark_price":0.055,"mark_iv":50,"bid_iv":49,"ask_iv":51,"underlying_price":100000}"""), store);
        DeribitMessageParser.Apply(Event("rest", "public/get_book_summary_by_currency", new { currency = "BTC", kind = "option" },
            """[{"instrument_name":"X","creation_timestamp":1760000002000,"bid_price":0.04,"ask_price":0.07,"mark_price":0.055,"mark_iv":50,"underlying_price":100000}]"""), store);
        Assert.AreEqual(QuoteSource.Ticker, store.GetQuote("X")!.Source);
        DeribitMessageParser.Apply(Event("rest", "public/get_book_summary_by_currency", new { currency = "BTC", kind = "option" },
            """[{"instrument_name":"X","creation_timestamp":1760000060000,"bid_price":0.04,"ask_price":0.07,"mark_price":0.055,"mark_iv":50,"underlying_price":100000}]"""), store);
        Assert.AreEqual(0.04, store.GetQuote("X")!.Bid);
    }

    [TestMethod]
    public async Task RecordThenReplay_ReproducesTheSameState()
    {
        var file = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}.jsonl.gz");
        try
        {
            var events = new[]
            {
                Event("rest", "public/get_index_price", new { index_name = "btc_usd" }, """{"index_price":100000}""", 1_760_000_000_000),
                Event("ws", "deribit_price_index.btc_usd", null, """{"index_name":"btc_usd","price":100250.5,"timestamp":1760000000100}""", 1_760_000_000_200)
            };
            await using (var recorder = new MarketRecorder(file))
                foreach (var e in events) recorder.Write(e);

            var store = new MarketDataStore();
            var feed = new ReplayMarketFeed(file, 0, store, e => DeribitMessageParser.Apply(e, store));
            await feed.RunAsync(CancellationToken.None);

            Assert.AreEqual(100_250.5, store.GetIndex("BTC"));
            Assert.AreEqual(2, store.Status.EventCount);
            Assert.IsTrue(store.Status.ReplayFinished);
            Assert.AreEqual(DeribitMessageParser.FromUnixMs(1_760_000_000_200), store.Now);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public async Task RateLimiter_RespectsRate()
    {
        var limiter = new TokenBucketRateLimiter(20, 1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 6; i++) await limiter.WaitAsync(CancellationToken.None);
        Assert.IsTrue(sw.ElapsedMilliseconds >= 230, $"{sw.ElapsedMilliseconds} ms");  // 5 attentes de 50 ms
    }
}
