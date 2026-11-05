// Tests de l'option sur mesure et de sa couverture : interpolation, refus hors plage, grecques neutralisées, prix client.
using DeribitPricer.Core.Hedging;
using DeribitPricer.Core.Pricing;

namespace DeribitPricer.Tests;

[TestClass]
public class CustomAndHedgeTests
{
    [TestMethod]
    public void Custom_OnListedStrikeAndExpiry_ReturnsMarketVol()
    {
        var store = TestMarket.Create();
        var v = CustomOptionPricer.Value(new CustomOptionSpec("BTC", OptionType.Call, 104_000, TestMarket.E1), store);
        Assert.IsTrue(v.IsSuccess, string.Join(" ", v.Errors));
        Assert.IsTrue(v.Value!.OnListedExpiry);
        Assert.AreEqual(TestMarket.Vol(TestMarket.E1, 104_000), v.Value.Vol, 1e-6);
        Assert.AreEqual(TestMarket.Forward(TestMarket.E1), v.Value.Forward, 1e-6);
    }

    [TestMethod]
    public void Custom_BetweenExpiries_InterpolatesForwardAndTotalVariance()
    {
        var store = TestMarket.Create();
        var expiry = new DateTime(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);
        var v = CustomOptionPricer.Value(new CustomOptionSpec("BTC", OptionType.Put, 97_000, expiry), store).Value!;

        double t = Black76.YearFraction(expiry, TestMarket.Now);
        double t1 = Black76.YearFraction(TestMarket.E1, TestMarket.Now), t2 = Black76.YearFraction(TestMarket.E2, TestMarket.Now);
        double w = (t - t1) / (t2 - t1);
        Assert.AreEqual(TestMarket.Forward(TestMarket.E1) + w * (TestMarket.Forward(TestMarket.E2) - TestMarket.Forward(TestMarket.E1)), v.Forward, 1e-6);

        double expectedVar = v.Slices[0].TotalVariance + w * (v.Slices[1].TotalVariance - v.Slices[0].TotalVariance);
        Assert.AreEqual(Math.Sqrt(expectedVar / t), v.Vol, 1e-12);
        Assert.AreEqual(v.LogMoneyness, Math.Log(v.Slices[0].EquivalentStrike / v.Slices[0].Forward), 1e-12);
        Assert.IsTrue(v.Vol > 0.4 && v.Vol < 0.6);
    }

    [TestMethod]
    public void Custom_RejectsOutOfRangeExpiryAndStrike()
    {
        var store = TestMarket.Create();
        var tooFar = CustomOptionPricer.Value(new CustomOptionSpec("BTC", OptionType.Call, 100_000, new DateTime(2027, 6, 1, 8, 0, 0, DateTimeKind.Utc)), store);
        StringAssert.Contains(tooFar.Errors[0], "hors de la plage cotée");
        var tooHigh = CustomOptionPricer.Value(new CustomOptionSpec("BTC", OptionType.Call, 150_000, TestMarket.E1), store);
        StringAssert.Contains(tooHigh.Errors[0], "hors de la plage des strikes");
    }

    [TestMethod]
    public void Hedge_NeutralisesGammaVegaThenDelta()
    {
        var store = TestMarket.Create();
        var expiry = new DateTime(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);
        var v = CustomOptionPricer.Value(new CustomOptionSpec("BTC", OptionType.Call, 104_000, expiry), store).Value!;
        var s = HedgePairSuggester.Suggest(v, store)!;
        Assert.AreNotEqual(s.Option1.ExpiryUtc, s.Option2.ExpiryUtc);

        var h = HedgeCalculator.Compute(v, -10, s.Option1, s.Option2, s.Future, store);
        Assert.IsTrue(h.IsSuccess, string.Join(" ", h.Errors));
        var before = h.Value!.GlobalGreeksBeforeRounding;
        Assert.AreEqual(0, before.Gamma, 1e-12);
        Assert.AreEqual(0, before.Vega, 1e-8);
        Assert.AreEqual(0, before.Delta, 1e-10);

        // Après arrondi au pas de 0,1 : petits résidus, quantités multiples du pas.
        foreach (var line in h.Value.Lines.Where(l => !l.IsFuture))
            Assert.AreEqual(0, Math.Abs(line.Quantity * 10 - Math.Round(line.Quantity * 10)), 1e-9);
        Assert.IsTrue(Math.Abs(h.Value.GlobalGreeks.Vega) < Math.Abs(h.Value.CustomGreeks.Vega) * 0.2);
        Assert.IsTrue(h.Value.HedgeCostUsd > 0);
    }

    [TestMethod]
    public void Hedge_RejectsSameExpiryPair()
    {
        // Même échéance et vol quasi plate : rapports Γ/V presque égaux, système non résoluble.
        var store = TestMarket.Create();
        var v = CustomOptionPricer.Value(new CustomOptionSpec("BTC", OptionType.Call, 100_000, TestMarket.E1), store).Value!;
        var o1 = store.FindOption("BTC", TestMarket.E1, 100_000, OptionType.Call)!;
        var o2 = store.FindOption("BTC", TestMarket.E1, 100_000, OptionType.Put)!;
        var future = store.GetFutures("BTC")[0];
        var h = HedgeCalculator.Compute(v, -1, o1, o2, future, store);
        Assert.IsFalse(h.IsSuccess);
        StringAssert.Contains(h.Errors[0], "Paire refusée");
    }

    [TestMethod]
    public void ClientPrice_AddsCostWhenClientBuys()
    {
        var store = TestMarket.Create();
        var v = CustomOptionPricer.Value(new CustomOptionSpec("BTC", OptionType.Call, 100_000, TestMarket.E1), store).Value!;
        Assert.AreEqual(v.TheoUsd + 10 + 5, HedgeCalculator.ClientPriceUsd(v, -2, 20, 5), 1e-9);
        Assert.AreEqual(v.TheoUsd - 10 - 5, HedgeCalculator.ClientPriceUsd(v, 2, 20, 5), 1e-9);
    }
}
