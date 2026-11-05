using DeribitPricer.Core.Market;
using DeribitPricer.Core.Pricing;
using DeribitPricer.Excel;

namespace DeribitPricer.Tests;

[TestClass]
public class VolatilityTests
{
    [TestMethod]
    public void Surface_CaptureMatchesOriginalPricer_AndFreezesMarket()
    {
        var store = TestMarket.Create();
        var surface = VolatilitySurface.Capture(store, "btc");
        var maturity = new DateTime(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);
        var expected = CustomOptionPricer.Value(new("BTC", OptionType.Call, 97_000, maturity), store).Value!;
        var actual = surface.Value(97_000, maturity).Value!;
        Assert.AreEqual(expected.Vol, actual.Vol, 1e-10);
        Assert.AreEqual(expected.Forward, actual.Forward, 1e-10);
        Assert.AreEqual(TestMarket.Vol(TestMarket.E1, 104_000), surface.Value(104_000, TestMarket.E1).Value!.Vol, 1e-6);
        store.ReplaceInstruments("BTC", InstrumentKind.Option, []);
        Assert.AreEqual(actual.Vol, surface.Value(97_000, maturity).Value!.Vol, 1e-12);
    }

    [TestMethod]
    public void Surface_InterpolatesTotalVariance_NotVolatility()
    {
        var now = TestMarket.Now;
        var a = new VolatilitySlice(now.AddDays(10), 100, now, now, [new(80, 0.4), new(120, 0.4)]);
        var b = new VolatilitySlice(now.AddDays(30), 120, now, now, [new(96, 0.8), new(144, 0.8)]);
        var surface = new VolatilitySurface("BTC", now, now, "Replay", 100, [a, b]);
        var v = surface.Value(110, now.AddDays(20)).Value!;
        Assert.AreEqual(110, v.Forward, 1e-12);
        Assert.AreEqual(Math.Sqrt((0.4 * 0.4 * 10 + 0.8 * 0.8 * 30) / 2 / 20), v.Vol, 1e-12);
        Assert.AreEqual(v.Vol, surface.VolAt(20, 1)!.Value, 1e-12);
        Assert.IsNull(surface.VolAt(5, 1));
        Assert.IsNull(surface.VolAt(40, 1));
        Assert.IsNull(surface.VolAt(20, 2));
        Assert.IsNull(surface.VolAt(double.NaN, 1));
        Assert.IsFalse(surface.Value(double.PositiveInfinity, now.AddDays(20)).IsSuccess);
    }

    [TestMethod]
    public void Surface_ComparisonUsesResidualTenor_AndEachDatesForward()
    {
        var a = VolatilitySurface.Capture(TestMarket.Create(), "BTC");
        var shift = TimeSpan.FromDays(30);
        var b = a with { AsOfUtc = a.AsOfUtc + shift,
            Slices = a.Slices.Select(s => s with { ExpiryUtc = s.ExpiryUtc + shift, Forward = s.Forward * 2,
                Points = s.Points.Select(p => new VolatilityPoint(p.Strike * 2, p.Vol + 0.1)).ToArray() }).ToArray() };
        Assert.AreEqual(0.1, b.VolAt(20, 1)!.Value - a.VolAt(20, 1)!.Value, 0.002);
        // Même échéance absolue, mais maturité résiduelle différente : elle n'est pas substituée au tenor.
        Assert.IsNull(b.ForwardAt(a.Slices[0].ExpiryUtc));
    }

    [TestMethod]
    public async Task Snapshots_SurviveRestart_PreserveHistoricalClock_RejectPathTraversal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pricer-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var surface = VolatilitySurface.Capture(TestMarket.Create(), "BTC");
            var saved = await new SurfaceSnapshotRepository(dir).SaveAsync(surface);
            var repository = new SurfaceSnapshotRepository(dir);
            var restored = (await repository.LoadAsync(saved.Id))!;
            Assert.AreEqual(surface.AsOfUtc, restored.Surface.AsOfUtc);
            Assert.AreEqual(surface.Value(100_000, TestMarket.E1).Value!.Vol, restored.Surface.Value(100_000, TestMarket.E1).Value!.Vol, 1e-12);
            Assert.AreEqual(1, (await repository.ListAsync("BTC")).Count);
            Assert.AreEqual(0, (await repository.ListAsync("ETH")).Count);
            Assert.IsNull(await repository.LoadAsync("../anything"));
            await File.WriteAllTextAsync(Path.Combine(dir, new string('a', 32) + ".json"), "truncated");
            Assert.AreEqual(1, (await repository.ListAsync("BTC")).Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void ExcelSurfaceTable_ContainsDatesNumbersMetadata_AndNoExtrapolatedWings()
    {
        var surface = VolatilitySurface.Capture(TestMarket.Create(), "BTC");
        var table = PricerApiClient.SurfaceTable(surface);
        StringAssert.Contains((string)table[0, 0]!, "BTC");
        StringAssert.Contains((string)table[0, 1]!, "2026-10-23 08:00 UTC");
        var strike = (double)table[2, 0]!;
        Assert.AreEqual(surface.Value(strike, TestMarket.E1).Value!.Vol, (double)table[2, 1]!, 1e-12);
        var limited = surface with { Slices = [surface.Slices[0], surface.Slices[1] with { Points = surface.Slices[1].Points.Where(p => p.Strike > 90_000).ToArray() }] };
        Assert.IsNull(PricerApiClient.SurfaceTable(limited)[2, 2]);
        Assert.AreEqual("2026-11-27", PricerApiClient.Maturity(TestMarket.E2.ToOADate()));
        Assert.AreEqual("2026-11-27", PricerApiClient.Maturity("2026-11-27"));
    }
}
