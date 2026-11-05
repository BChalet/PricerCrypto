using DeribitPricer.Core.Feeds;
using DeribitPricer.Core.Market;
using DeribitPricer.Core.Pricing;

namespace DeribitPricer.App.Services;

public sealed class VolatilityService(MarketDataService market, SurfaceSnapshotRepository snapshots)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Version, long Second, VolatilitySurface Surface)> _cache = new();

    public VolatilitySurface Current(string currency)
    {
        currency = currency.Trim().ToUpperInvariant();
        lock (_gate)
        {
            var version = market.Store.Version;
            var second = market.Store.Now.Ticks / TimeSpan.TicksPerSecond;
            if (_cache.TryGetValue(currency, out var entry) && entry.Version == version && entry.Second == second)
                return entry.Surface;
            var surface = VolatilitySurface.Capture(market.Store, currency);
            // Ne conserver que les sous-jacents réellement présents dans le flux.
            if (surface.Slices.Count > 0) _cache[currency] = (version, second, surface);
            return surface;
        }
    }

    public async Task<VolatilitySurface?> GetAsync(string currency, string? snapshotId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId)) return Current(currency);
        var snapshot = await snapshots.LoadAsync(snapshotId, ct);
        return snapshot?.Surface.Currency.Equals(currency.Trim(), StringComparison.OrdinalIgnoreCase) == true
            ? snapshot.Surface : null;
    }
}

public sealed class SurfaceHistoryOptions
{
    public string Directory { get; set; } = "data/volatility";
    public int CaptureSeconds { get; set; } = 300;
}

// En live seulement : conserver périodiquement les nappes pour les comparaisons futures.
public sealed class SurfaceHistoryHostedService(MarketDataService market, VolatilityService volatility,
    SurfaceSnapshotRepository snapshots, SurfaceHistoryOptions options, ILogger<SurfaceHistoryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (market.Options.IsReplay || options.CaptureSeconds <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(30, options.CaptureSeconds)));
        do
        {
            foreach (var currency in market.Store.Currencies)
            {
                try
                {
                    var surface = volatility.Current(currency);
                    // Ne pas archiver automatiquement un marché arrêté ou sans cotation récente.
                    if (surface.Slices.Count > 0 && surface.LastEventUtc is { } ts &&
                        (DateTime.UtcNow - ts).TotalSeconds < 60 &&
                        surface.Slices.Any(s => s.LatestQuoteUtc is { } q && (DateTime.UtcNow - q).TotalSeconds < 60))
                        await snapshots.SaveAsync(surface, stoppingToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Impossible d'archiver la nappe {Currency}", currency);
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
