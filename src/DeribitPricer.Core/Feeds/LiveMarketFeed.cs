// Le flux live : au démarrage on charge tout en REST, puis on suit le marché en WebSocket et on rafraîchit le résumé toutes les 5 secondes.
using DeribitPricer.Core.Market;

namespace DeribitPricer.Core.Feeds;

// Flux live Deribit :
// - REST au démarrage puis périodiquement : référentiels, résumé de toutes les options, indices, prix de règlement ;
// - WebSocket : indice et futures en continu, tickers complets des instruments suivis.
public sealed class LiveMarketFeed : IMarketDataFeed
{
    private readonly MarketDataOptions _options;
    private readonly MarketDataStore _store;
    private readonly Action<MarketEvent> _publish;
    private readonly DeribitRestClient _rest;
    private readonly DeribitWebSocketClient _ws;
    private readonly object _watchLock = new();
    private IReadOnlyCollection<string> _watched = Array.Empty<string>();
    private CancellationToken _ct;

    public LiveMarketFeed(MarketDataOptions options, MarketDataStore store, Action<MarketEvent> publish, HttpClient? http = null)
    {
        _options = options;
        _store = store;
        _publish = publish;
        var limiter = new TokenBucketRateLimiter(options.MaxRequestsPerSecond, options.MaxBurst);
        http ??= new HttpClient();
        http.BaseAddress ??= new Uri(options.RestBaseUrl);
        http.Timeout = TimeSpan.FromSeconds(20);
        _rest = new DeribitRestClient(http, limiter);
        _ws = new DeribitWebSocketClient(new Uri(options.WebSocketUrl), limiter, store.Status, publish);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _ct = ct;
        _store.UseWallClock = true;
        _store.Status.Mode = "Live";
        _store.Status.State = "Chargement des référentiels";

        foreach (var currency in _options.Currencies)
        {
            await SafeAsync(() => LoadReferenceDataAsync(currency, ct));
            await SafeAsync(() => PollSummaryAsync(currency, ct));
        }

        var wsTask = _ws.RunAsync(DesiredChannels, ct);
        var pollTask = PollLoopAsync(ct);
        await Task.WhenAll(wsTask, pollTask);
    }

    public void SetWatchedInstruments(IReadOnlyCollection<string> instrumentNames)
    {
        lock (_watchLock) _watched = instrumentNames.ToArray();
        _ = SafeAsync(() => _ws.SyncSubscriptionsAsync(_ct));
    }

    // Canaux WebSocket souhaités : indices, tous les futures, instruments suivis.
    private IReadOnlyCollection<string> DesiredChannels()
    {
        var channels = new HashSet<string>();
        foreach (var currency in _options.Currencies)
        {
            channels.Add($"deribit_price_index.{IndexNames.ForCurrency(currency)}");
            foreach (var future in _store.GetFutures(currency))
                channels.Add($"ticker.{future.Name}.{_options.TickerInterval}");
        }
        lock (_watchLock)
            foreach (var name in _watched)
                channels.Add($"ticker.{name}.{_options.TickerInterval}");
        return channels;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var lastReferenceRefresh = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(_options.SummaryPollSeconds), ct); }
            catch (OperationCanceledException) { break; }

            // Les échéances Deribit tombent à 08:00 UTC : juste après, on recharge chaque minute référentiels
            // et prix de règlement pour régler rapidement les options échues.
            var sinceRefresh = DateTime.UtcNow - lastReferenceRefresh;
            var timeOfDay = DateTime.UtcNow.TimeOfDay;
            bool justAfterExpiry = timeOfDay >= TimeSpan.FromHours(8) && timeOfDay < TimeSpan.FromHours(8.25);
            bool refreshReference = sinceRefresh > TimeSpan.FromMinutes(_options.ReferenceRefreshMinutes)
                                    || (justAfterExpiry && sinceRefresh > TimeSpan.FromMinutes(1));
            foreach (var currency in _options.Currencies)
            {
                if (refreshReference) await SafeAsync(() => LoadReferenceDataAsync(currency, ct));
                await SafeAsync(() => PollSummaryAsync(currency, ct));
                if (!_ws.IsConnected) await SafeAsync(() => PollIndexAsync(currency, ct));
            }
            if (refreshReference)
            {
                lastReferenceRefresh = DateTime.UtcNow;
                await SafeAsync(() => _ws.SyncSubscriptionsAsync(ct));
            }
            if (_ws.IsConnected) _store.Status.State = "Connecté";
        }
    }

    private async Task LoadReferenceDataAsync(string currency, CancellationToken ct)
    {
        _publish(await _rest.CallAsync("public/get_instruments",
            new Dictionary<string, string> { ["currency"] = currency, ["kind"] = "option", ["expired"] = "false" }, ct));
        _publish(await _rest.CallAsync("public/get_instruments",
            new Dictionary<string, string> { ["currency"] = currency, ["kind"] = "future", ["expired"] = "false" }, ct));
        await PollIndexAsync(currency, ct);
        _publish(await _rest.CallAsync("public/get_delivery_prices",
            new Dictionary<string, string> { ["index_name"] = IndexNames.ForCurrency(currency), ["offset"] = "0", ["count"] = "10" }, ct));
    }

    private async Task PollSummaryAsync(string currency, CancellationToken ct) =>
        _publish(await _rest.CallAsync("public/get_book_summary_by_currency",
            new Dictionary<string, string> { ["currency"] = currency, ["kind"] = "option" }, ct));

    private async Task PollIndexAsync(string currency, CancellationToken ct) =>
        _publish(await _rest.CallAsync("public/get_index_price",
            new Dictionary<string, string> { ["index_name"] = IndexNames.ForCurrency(currency) }, ct));

    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _store.Status.Log($"Erreur : {ex.Message}");
            _store.Status.State = "Erreur réseau (voir journal)";
        }
    }
}
