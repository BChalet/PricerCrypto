// Tests du booking et du book : trade, débouclage, règlement à l'échéance, sauvegarde, exports.
using DeribitPricer.Core.Booking;
using DeribitPricer.Core.Pricing;
using DeribitPricer.Core.Structures;

namespace DeribitPricer.Tests;

[TestClass]
public class BookingTests
{
    private static Trade BookCallSpread(DeribitPricer.Core.Market.MarketDataStore store, Blotter blotter, double qty = 1)
    {
        var def = StructureBuilder.Build(new StructureRequest("BTC", StructureType.CallSpread, TestMarket.E1, [100_000, 110_000], TradeDirection.Buy, qty), store).Value!;
        return blotter.Add(TradeFactory.FromStructure(StructurePricer.Price(def, store), store).Value!);
    }

    [TestMethod]
    public void Trade_RecordsLegsAndPremium()
    {
        var store = TestMarket.Create();
        var blotter = new Blotter();
        var trade = BookCallSpread(store, blotter, 2);
        Assert.AreEqual("T00001", trade.Id);
        Assert.AreEqual(2, trade.Legs[0].SignedQuantity);
        Assert.AreEqual(-2, trade.Legs[1].SignedQuantity);
        Assert.AreEqual(store.GetQuote(trade.Legs[0].Instrument)!.Ask, trade.Legs[0].ExecutionPrice);
        Assert.AreEqual(store.GetQuote(trade.Legs[1].Instrument)!.Bid, trade.Legs[1].ExecutionPrice);
        Assert.AreEqual(2 * trade.Legs[0].ExecutionPrice - 2 * trade.Legs[1].ExecutionPrice, trade.PremiumCoin, 1e-12);
    }

    [TestMethod]
    public void Book_PnlEqualsValueMinusPremium_AndUnwindRealizes()
    {
        var store = TestMarket.Create();
        var blotter = new Blotter();
        var trade = BookCallSpread(store, blotter);

        var book = BookCalculator.Compute(blotter.Trades, store);
        var s = book.Structures.Single();
        Assert.AreEqual(s.MarkValueCoin - trade.PremiumCoin, s.PnlMarkCoin, 1e-12);
        Assert.IsTrue(s.PnlMarkCoin < 0);  // acheté à l'ask, valorisé au mark

        // Débouclage de la moitié au prix exécutable : la perte de fourchette est réalisée au prorata.
        var unwind = TradeFactory.Unwind(trade, 0.5, blotter.OpenQuantity(trade.Id), store);
        Assert.IsTrue(unwind.IsSuccess, string.Join(" ", unwind.Errors));
        blotter.Add(unwind.Value!);
        Assert.AreEqual(0.5, blotter.OpenQuantity(trade.Id), 1e-12);

        s = BookCalculator.Compute(blotter.Trades, store).Structures.Single();
        Assert.AreEqual(0.5, s.OpenQuantity, 1e-12);
        double expectedRealized = 0.5 * (store.GetQuote(trade.Legs[0].Instrument)!.Bid!.Value - trade.Legs[0].ExecutionPrice)
                                  - 0.5 * (store.GetQuote(trade.Legs[1].Instrument)!.Ask!.Value - trade.Legs[1].ExecutionPrice);
        Assert.AreEqual(expectedRealized, s.RealizedCoin, 1e-12);

        Assert.IsFalse(TradeFactory.Unwind(trade, 0.6, blotter.OpenQuantity(trade.Id), store).IsSuccess);
    }

    [TestMethod]
    public void Book_SettlesExpiredOptionsAtDeliveryPrice()
    {
        var store = TestMarket.Create();
        var blotter = new Blotter();
        var trade = BookCallSpread(store, blotter);

        store.AdvanceClock(TestMarket.E1.AddMinutes(5));
        var pending = BookCalculator.Compute(blotter.Trades, store).Structures.Single();
        Assert.IsTrue(pending.Legs.All(l => l.Status == LegStatus.PendingSettlement));

        store.SetDeliveryPrice("BTC", DateOnly.FromDateTime(TestMarket.E1), 105_000);
        var book = BookCalculator.Compute(blotter.Trades, store);
        var s = book.Structures.Single();
        Assert.IsFalse(s.IsOpen);
        Assert.AreEqual(2, book.SettledLegs.Count);
        // Payoff : (105 000 − 100 000) / 105 000 BTC sur le call long, rien sur le call vendu.
        Assert.AreEqual(5_000.0 / 105_000 - trade.PremiumCoin, s.RealizedCoin, 1e-12);
        Assert.AreEqual(s.RealizedCoin, s.PnlMarkCoin, 1e-12);
        Assert.AreEqual(0, book.GreeksByExpiry.Count);
    }

    [TestMethod]
    public void Blotter_PersistsToJson()
    {
        var file = Path.Combine(Path.GetTempPath(), $"blotter-{Guid.NewGuid():N}.json");
        try
        {
            var store = TestMarket.Create();
            var blotter = new Blotter(file);
            var trade = BookCallSpread(store, blotter);
            var reloaded = new Blotter(file);
            Assert.AreEqual(trade.Id, reloaded.Trades.Single().Id);
            Assert.AreEqual(trade.PremiumCoin, reloaded.Trades.Single().PremiumCoin, 1e-15);
            Assert.AreEqual(StructureType.CallSpread, reloaded.Trades.Single().StructureType);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public void Exports_ProduceCsvAndExcel()
    {
        var store = TestMarket.Create();
        var blotter = new Blotter();
        BookCallSpread(store, blotter);
        var book = BookCalculator.Compute(blotter.Trades, store);
        var csv = DeribitPricer.Core.Export.BookExporter.BlotterCsv(blotter.Trades);
        Assert.AreEqual(3, csv.Trim().Split('\n').Length);  // en-tête + 2 jambes
        StringAssert.Contains(DeribitPricer.Core.Export.BookExporter.BookCsv(book), "GRECQUES PAR ÉCHÉANCE");
        var xlsx = DeribitPricer.Core.Export.BookExporter.Workbook(blotter.Trades, book);
        Assert.IsTrue(xlsx.Length > 1000 && xlsx[0] == (byte)'P' && xlsx[1] == (byte)'K');  // fichier zip OOXML
    }
}
