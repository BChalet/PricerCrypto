// L'aiguillage des données : on choisit live ou rejeu selon la config, et chaque message va au parseur et, si besoin, à l'enregistreur.
using System.Collections.Concurrent;
using DeribitPricer.Core.Market;

namespace DeribitPricer.Core.Feeds;

// Point d'entrée des données de marché : choisit le flux selon la configuration (live ou rejeu),
// applique chaque message au store et l'enregistre si demandé.
public sealed class MarketDataService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, string[]> _watchByOwner = new();
    private readonly Func<string, string> _resolvePath;
    private IMarketDataFeed? _feed;
    private MarketRecorder? _recorder;

    public MarketDataService(MarketDataOptions options, Func<string, string>? resolvePath = null)
    {
        Options = options;
        _resolvePath = resolvePath ?? Path.GetFullPath;
        Store.UseWallClock = !options.IsReplay;
        Store.Status.Mode = options.IsReplay ? "Replay" : "Live";
    }

    public MarketDataOptions Options { get; }
    public MarketDataStore Store { get; } = new();

    public async Task RunAsync(CancellationToken ct)
    {
        if (Options.IsReplay)
        {
            _feed = new ReplayMarketFeed(_resolvePath(Options.ReplayFile), Options.ReplaySpeed, Store, Publish);
        }
        else
        {
            if (Options.Record)
            {
                var file = Path.Combine(_resolvePath(Options.RecordDirectory),
                    $"deribit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl.gz");
                _recorder = new MarketRecorder(file);
                Store.Status.Recording = true;
                Store.Status.RecordFile = file;
                Store.Status.Log($"Enregistrement dans {file}");
            }
            _feed = new LiveMarketFeed(Options, Store, Publish);
        }

        PushWatchList();
        await _feed.RunAsync(ct);
    }

    // Déclare les instruments qu'un écran (owner) veut suivre en temps réel.
    public void Watch(string owner, IEnumerable<string> instrumentNames)
    {
        var names = instrumentNames.Distinct().OrderBy(n => n).ToArray();
        if (_watchByOwner.TryGetValue(owner, out var current) && current.SequenceEqual(names)) return;
        _watchByOwner[owner] = names;
        PushWatchList();
    }

    public void Unwatch(string owner)
    {
        if (_watchByOwner.TryRemove(owner, out _)) PushWatchList();
    }

    private void PushWatchList() =>
        _feed?.SetWatchedInstruments(_watchByOwner.Values.SelectMany(n => n).ToHashSet());

    private void Publish(MarketEvent e)
    {
        try
        {
            DeribitMessageParser.Apply(e, Store);
        }
        catch (Exception ex)
        {
            Store.Status.Log($"Message illisible ({e.Name}) : {ex.Message}");
        }
        _recorder?.Write(e);
    }

    public async ValueTask DisposeAsync()
    {
        if (_recorder != null)
        {
            await _recorder.DisposeAsync();
            _recorder = null;
            Store.Status.Recording = false;
        }
    }
}
