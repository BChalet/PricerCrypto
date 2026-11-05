// Calcule le book à la demande et dit au flux de suivre en direct les instruments qu'on a en position.
using DeribitPricer.Core.Booking;
using DeribitPricer.Core.Feeds;

namespace DeribitPricer.App.Services;

// Calcule l'état du book et demande au flux de suivre en temps réel les instruments en position.
public sealed class BookService
{
    private readonly Blotter _blotter;
    private readonly MarketDataService _market;

    public BookService(Blotter blotter, MarketDataService market)
    {
        _blotter = blotter;
        _market = market;
        _blotter.Changed += UpdateWatchList;
        UpdateWatchList();
    }

    public BookSnapshot Compute() => BookCalculator.Compute(_blotter.Trades, _market.Store);

    private void UpdateWatchList()
    {
        var names = _blotter.Trades.SelectMany(t => t.Legs)
            .Where(l => l.Kind != LegKind.CustomOption)
            .Select(l => l.Instrument);
        _market.Watch("book", names);
    }
}
