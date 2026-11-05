// Tests des structures : construction des jambes, saisies refusées, prix exécutables, non traitable.
using DeribitPricer.Core.Pricing;
using DeribitPricer.Core.Structures;

namespace DeribitPricer.Tests;

[TestClass]
public class StructureTests
{
    private static StructureRequest Request(StructureType type, double[] strikes, double qty = 1, TradeDirection dir = TradeDirection.Buy) =>
        new("BTC", type, TestMarket.E1, strikes, dir, qty);

    [TestMethod]
    public void Builds_CallSpread()
    {
        var store = TestMarket.Create();
        var result = StructureBuilder.Build(Request(StructureType.CallSpread, [100_000, 110_000]), store);
        Assert.IsTrue(result.IsSuccess, string.Join(" ", result.Errors));
        var legs = result.Value!.Legs;
        Assert.AreEqual(2, legs.Count);
        Assert.AreEqual(100_000, legs[0].Instrument.Strike);
        Assert.AreEqual(1, legs[0].Ratio);
        Assert.AreEqual(-1, legs[1].Ratio);
    }

    [TestMethod]
    public void PutSpread_BuysHigherStrike()
    {
        var store = TestMarket.Create();
        var legs = StructureBuilder.Build(Request(StructureType.PutSpread, [90_000, 100_000]), store).Value!.Legs;
        Assert.AreEqual(100_000, legs.Single(l => l.Ratio > 0).Instrument.Strike);
    }

    [TestMethod]
    public void Rejects_UnlistedStrike_WrongOrder_AsymmetricButterfly_BadQuantity()
    {
        var store = TestMarket.Create();
        StringAssert.Contains(StructureBuilder.Build(Request(StructureType.CallSpread, [100_000, 101_000]), store).Errors[0], "n'est pas coté");
        StringAssert.Contains(StructureBuilder.Build(Request(StructureType.CallSpread, [110_000, 100_000]), store).Errors[0], "mauvais ordre");
        StringAssert.Contains(StructureBuilder.Build(Request(StructureType.CallButterfly, [96_000, 100_000, 106_000]), store).Errors[0], "non symétrique");
        StringAssert.Contains(StructureBuilder.Build(Request(StructureType.CallSpread, [100_000, 110_000], 0.15), store).Errors[0], "pas de cotation");
        StringAssert.Contains(StructureBuilder.Build(Request(StructureType.CallSpread, [100_000, 110_000], 0.05), store).Errors[0], "minimum");
    }

    [TestMethod]
    public void Pricer_ExecutablePrices_SpreadAndGreeks()
    {
        var store = TestMarket.Create();
        var def = StructureBuilder.Build(Request(StructureType.CallButterfly, [96_000, 100_000, 104_000], 2), store).Value!;
        var p = StructurePricer.Price(def, store);

        var q = def.Legs.Select(l => store.GetQuote(l.Instrument.Name)!).ToList();
        double buy = q[0].Ask!.Value - 2 * q[1].Bid!.Value + q[2].Ask!.Value;
        double sell = q[0].Bid!.Value - 2 * q[1].Ask!.Value + q[2].Bid!.Value;
        Assert.AreEqual(buy, p.BuyPriceCoinUnit!.Value, 1e-12);
        Assert.AreEqual(sell, p.SellPriceCoinUnit!.Value, 1e-12);
        Assert.AreEqual(buy - sell, p.SpreadCoinUnit!.Value, 1e-12);
        Assert.AreEqual(buy * 2, p.PremiumCoin!.Value, 1e-12);
        Assert.IsTrue(p.Tradable);

        // Théo = mark (le marché de test est construit avec Black-76) : aucun écart signalé.
        Assert.IsTrue(p.Legs.All(l => !l.Analytics!.MarkDiffFlag && !l.Analytics.MarkVol.Flag));
        var expected = Greeks.Sum(p.Legs.Select(l => l.SignedQuantity * l.Analytics!.Greeks!.Value));
        Assert.AreEqual(expected.Vega, p.Greeks!.Value.Vega, 1e-9);
    }

    [TestMethod]
    public void Pricer_LegWithoutAsk_IsNotTradable()
    {
        var store = TestMarket.Create();
        var def = StructureBuilder.Build(Request(StructureType.CallSpread, [100_000, 110_000]), store).Value!;
        var name = def.Legs[0].Instrument.Name;
        store.SetQuote(store.GetQuote(name)! with { Ask = null });
        var p = StructurePricer.Price(def, store);
        Assert.IsFalse(p.Tradable);
        Assert.IsTrue(p.NotTradableReasons.Any(r => r.Contains("pas d'ask")));
    }
}
