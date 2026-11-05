// Tests des maths : loi normale, Black-76, parité call-put, grecques, vol implicite et profil de P&L.
using DeribitPricer.Core.Pricing;

namespace DeribitPricer.Tests;

[TestClass]
public class PricingTests
{
    [TestMethod]
    public void NormalCdf_KnownValues()
    {
        Assert.AreEqual(0.5, NormalDistribution.Cdf(0), 1e-15);
        Assert.AreEqual(0.841344746068543, NormalDistribution.Cdf(1), 1e-14);
        Assert.AreEqual(0.0227501319481792, NormalDistribution.Cdf(-2), 1e-14);
        Assert.AreEqual(0.999968328758167, NormalDistribution.Cdf(4), 1e-14);
    }

    [TestMethod]
    public void Black76_AtTheMoney_MatchesClosedForm()
    {
        // F = K : C = F (2 N(σ√T / 2) − 1)
        double c = Black76.PriceUsd(OptionType.Call, 100, 100, 0.2, 1);
        Assert.AreEqual(100 * (2 * NormalDistribution.Cdf(0.1) - 1), c, 1e-10);
        Assert.AreEqual(7.965567455405804, c, 1e-9);
    }

    [TestMethod]
    public void Black76_PutCallParity()
    {
        foreach (var k in new[] { 80_000.0, 100_000, 130_000 })
        {
            double c = Black76.PriceUsd(OptionType.Call, 100_000, k, 0.6, 0.25);
            double p = Black76.PriceUsd(OptionType.Put, 100_000, k, 0.6, 0.25);
            Assert.AreEqual(100_000 - k, c - p, 1e-6);
        }
    }

    [TestMethod]
    public void Greeks_MatchFiniteDifferences()
    {
        double f = 100_000, k = 105_000, s = 0.55, t = 30 / 365.0;
        var g = Black76.ComputeGreeks(OptionType.Call, f, k, s, t);
        double h = 1;
        double delta = (Black76.PriceUsd(OptionType.Call, f + h, k, s, t) - Black76.PriceUsd(OptionType.Call, f - h, k, s, t)) / (2 * h);
        double gamma = (Black76.PriceUsd(OptionType.Call, f + h, k, s, t) - 2 * Black76.PriceUsd(OptionType.Call, f, k, s, t)
                        + Black76.PriceUsd(OptionType.Call, f - h, k, s, t)) / (h * h);
        double vega = (Black76.PriceUsd(OptionType.Call, f, k, s + 0.005, t) - Black76.PriceUsd(OptionType.Call, f, k, s - 0.005, t));  // pour 1 point
        double theta = Black76.PriceUsd(OptionType.Call, f, k, s, t - 1 / 365.0) - Black76.PriceUsd(OptionType.Call, f, k, s, t);   // 1 jour

        Assert.AreEqual(delta, g.Delta, 1e-6);
        Assert.AreEqual(gamma, g.Gamma, 1e-8);
        Assert.AreEqual(vega, g.Vega, 1e-2);
        Assert.AreEqual(theta, g.Theta, Math.Abs(theta) * 0.02);
        Assert.AreEqual(g.Delta - 1, Black76.ComputeGreeks(OptionType.Put, f, k, s, t).Delta, 1e-12);
    }

    [TestMethod]
    public void ImpliedVol_RoundTrip()
    {
        foreach (var type in new[] { OptionType.Call, OptionType.Put })
        foreach (var k in new[] { 60_000.0, 90_000, 100_000, 115_000, 180_000 })
        foreach (var vol in new[] { 0.05, 0.4, 1.2, 3.0 })
        foreach (var t in new[] { 1 / 365.0, 0.25, 2 })
        {
            double price = Black76.PriceUsd(type, 100_000, k, vol, t);
            if (price - Black76.Intrinsic(type, 100_000, k) < 1e-4) continue;  // prix collé à la borne : vol non identifiable
            var result = ImpliedVolSolver.Solve(type, price, 100_000, k, t);
            Assert.IsTrue(result.Success, $"{type} K={k} σ={vol} T={t} : {result.Error}");
            double repriced = Black76.PriceUsd(type, 100_000, k, result.Vol!.Value, t);
            Assert.AreEqual(price, repriced, 1e-6);
        }
    }

    [TestMethod]
    public void ImpliedVol_FromCoinPrice()
    {
        double f = 100_000, k = 110_000, t = 0.1;
        double coin = Black76.PriceCoin(OptionType.Call, f, k, 0.47, t);
        var result = ImpliedVolSolver.SolveFromCoinPrice(OptionType.Call, coin, f, k, t);
        Assert.AreEqual(0.47, result.Vol!.Value, 1e-8);
    }

    [TestMethod]
    public void ImpliedVol_RejectsPricesOutsideNoArbitrageBounds()
    {
        Assert.IsFalse(ImpliedVolSolver.Solve(OptionType.Call, 9_000, 100_000, 90_000, 0.1).Success);   // < intrinsèque
        Assert.IsFalse(ImpliedVolSolver.Solve(OptionType.Call, 100_000, 100_000, 90_000, 0.1).Success); // = F
        Assert.IsFalse(ImpliedVolSolver.Solve(OptionType.Put, 0, 100_000, 90_000, 0.1).Success);
        Assert.IsFalse(ImpliedVolSolver.Solve(OptionType.Put, 500, 100_000, 90_000, 0).Success);        // T = 0
    }

    [TestMethod]
    public void ImpliedVol_FallsBackToBisectionWhenVegaIsTiny()
    {
        // Option très en dehors de la monnaie : vega quasi nul au point de départ de 50 %.
        double price = Black76.PriceUsd(OptionType.Call, 100_000, 300_000, 1.5, 0.05);
        var result = ImpliedVolSolver.Solve(OptionType.Call, price, 100_000, 300_000, 0.05);
        Assert.IsTrue(result.Success);
        Assert.AreEqual(1.5, result.Vol!.Value, 1e-4);
    }

    [TestMethod]
    public void Profile_CallSpread_MaxGainLossAndBreakeven()
    {
        var legs = new List<ProfileLeg>
        {
            new(OptionType.Call, 100_000, 1, 0.5),
            new(OptionType.Call, 110_000, -1, 0.5)
        };
        // Prime de 0,03 BTC à F = 100 000 : 3 000 USD.
        var p = PayoffProfileCalculator.Compute(legs, 100_000, 0.1, 0.03);
        Assert.AreEqual(7_000, p.MaxGainUsd!.Value, 1e-6);
        Assert.AreEqual(-3_000, p.MaxLossUsd!.Value, 1e-6);
        Assert.AreEqual(1, p.Breakevens.Count);
        Assert.AreEqual(103_000, p.Breakevens[0], 1e-6);
        Assert.AreEqual(13, p.Points.Count);  // −30 % à +30 % par pas de 5 %
    }

    [TestMethod]
    public void Profile_ShortCall_HasUnboundedLoss()
    {
        var p = PayoffProfileCalculator.Compute([new ProfileLeg(OptionType.Call, 100_000, -1, 0.5)], 100_000, 0.1, -0.02);
        Assert.IsNull(p.MaxLossUsd);
        Assert.AreEqual(2_000, p.MaxGainUsd!.Value, 1e-6);
        Assert.AreEqual(102_000, p.Breakevens.Single(), 1e-6);
    }

    [TestMethod]
    public void Profile_Butterfly_HasTwoBreakevens()
    {
        var legs = new List<ProfileLeg>
        {
            new(OptionType.Put, 90_000, 1, 0.5), new(OptionType.Put, 100_000, -2, 0.5), new(OptionType.Put, 110_000, 1, 0.5)
        };
        var p = PayoffProfileCalculator.Compute(legs, 100_000, 0.1, 0.02);
        CollectionAssert.AreEqual(new[] { 92_000.0, 108_000.0 }, p.Breakevens.Select(b => Math.Round(b, 6)).ToArray());
        Assert.AreEqual(8_000, p.MaxGainUsd!.Value, 1e-6);
        Assert.AreEqual(-2_000, p.MaxLossUsd!.Value, 1e-6);
    }
}
