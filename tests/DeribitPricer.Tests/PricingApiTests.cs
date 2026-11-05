using System.Net;
using System.Text.Json;
using DeribitPricer.App.Services;
using DeribitPricer.Core.Feeds;
using DeribitPricer.Core.Market;
using DeribitPricer.Core.Pricing;
using DeribitPricer.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DeribitPricer.Tests;

[TestClass]
public class PricingApiTests
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "pricer-api-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) &&
                    (d.ImplementationType == typeof(MarketDataHostedService) || d.ImplementationType == typeof(SurfaceHistoryHostedService))).ToArray())
                    services.Remove(descriptor);
                services.RemoveAll<MarketDataService>();
                var market = new MarketDataService(new() { Mode = "Replay" });
                var reference = TestMarket.Create();
                market.Store.AdvanceClock(TestMarket.Now);
                market.Store.SetIndex("BTC", TestMarket.Spot);
                foreach (var instrument in reference.GetAllOptions("BTC"))
                {
                    market.Store.UpsertInstrument(instrument);
                    market.Store.SetQuote(reference.GetQuote(instrument.Name)!);
                }
                services.AddSingleton(market);
                services.RemoveAll<SurfaceSnapshotRepository>();
                services.AddSingleton(new SurfaceSnapshotRepository(DirectoryPath));
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        }
    }

    [TestMethod]
    public async Task ExcelClient_UsesRealApi_ForPriceIvSurface_AndHistoricalSnapshot()
    {
        using var factory = new Factory();
        using var http = factory.CreateClient();
        var client = new PricerApiClient(http, http.BaseAddress!);
        const string maturity = "2026-10-23";
        var vol = await client.VolAsync("BTC", 104_000, maturity);
        Assert.AreEqual(TestMarket.Vol(TestMarket.E1, 104_000), vol, 1e-6);
        var f = TestMarket.Forward(TestMarket.E1);
        var expected = Black76.PriceUsd(OptionType.Call, f, 104_000, vol, Black76.YearFraction(TestMarket.E1, TestMarket.Now));
        Assert.AreEqual(expected, await client.PriceAsync("Call", maturity, "BTC", 104_000), 1e-7);
        Assert.AreEqual(expected / f, await client.PriceAsync("C", maturity, "BTC", 104_000, unit: "COIN"), 1e-10);
        var surface = await client.SurfaceAsync("BTC");
        Assert.AreEqual(2, surface.Slices.Count);
        Assert.AreEqual("Replay", surface.Mode);
        var repo = factory.Services.GetRequiredService<SurfaceSnapshotRepository>();
        var saved = await repo.SaveAsync(surface);
        factory.Services.GetRequiredService<MarketDataService>().Store.ReplaceInstruments("BTC", InstrumentKind.Option, []);
        Assert.AreEqual(vol, await client.VolAsync("BTC", 104_000, maturity, saved.Id), 1e-12);
        var list = await http.GetStringAsync("/api/snapshots?underlying=BTC");
        StringAssert.Contains(list, saved.Id);
        Assert.AreEqual(HttpStatusCode.NotFound, (await http.GetAsync($"/api/volsurface?underlying=ETH&snapshotId={saved.Id}")).StatusCode);
    }

    [TestMethod]
    public async Task Api_ManualPriceHonoursZeroVol_AndRejectsInvalidOrMissingMarketInputs()
    {
        using var factory = new Factory();
        using var http = factory.CreateClient();
        var client = new PricerApiClient(http, http.BaseAddress!);
        Assert.AreEqual(10_000, await client.PriceAsync("Call", "2026-10-23", "BTC", 100_000, 0, 110_000), 1e-10);
        foreach (var path in new[]
        {
            "/api/priceoption?optionType=Wrong&maturity=2026-10-23&underlying=BTC&strike=100000",
            "/api/priceoption?optionType=Call&maturity=not-a-date&underlying=BTC&strike=100000",
            "/api/priceoption?optionType=Call&maturity=2026-10-23&underlying=BTC&strike=-1",
            "/api/priceoption?optionType=Call&maturity=2026-10-23&underlying=BTC&strike=100000&volatility=-0.1",
            "/api/priceoption?optionType=Call&maturity=2026-10-23&underlying=BTC&strike=100000&forward=110000",
            "/api/volimpli?underlying=BTC&strike=100000&maturity=2027-01-01",
            "/api/volimpli?underlying=BTC&strike=999999&maturity=2026-10-23",
            "/api/volsurface?underlying=NOT_AVAILABLE"
        })
        {
            var response = await http.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, path);
            Assert.IsTrue(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.TryGetProperty("error", out _), path);
        }
        Assert.IsTrue(PricingApi.TryMaturity("2026-11-27T09:00:00+01:00", out var expiry));
        Assert.AreEqual(TestMarket.E2, expiry);
        Assert.IsFalse(PricingApi.TryMaturity("2026-11-27T08:00:00", out _));
    }
}
