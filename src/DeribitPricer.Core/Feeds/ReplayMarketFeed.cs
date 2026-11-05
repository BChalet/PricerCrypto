// Le rejeu : on relit le fichier enregistré en respectant les délais d'origine, éventuellement accélérés.
using System.Diagnostics;
using DeribitPricer.Core.Market;

namespace DeribitPricer.Core.Feeds;

// Rejoue un fichier enregistré en respectant les délais d'origine entre messages,
// divisés par le facteur de vitesse (ReplaySpeed <= 0 : aussi vite que possible).
// Les messages passent par le même parseur que le live : l'outil se comporte à l'identique.
public sealed class ReplayMarketFeed : IMarketDataFeed
{
    private readonly string _path;
    private readonly double _speed;
    private readonly MarketDataStore _store;
    private readonly Action<MarketEvent> _publish;

    public ReplayMarketFeed(string path, double speed, MarketDataStore store, Action<MarketEvent> publish)
    {
        _path = path;
        _speed = speed;
        _store = store;
        _publish = publish;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var status = _store.Status;
        _store.UseWallClock = false;
        status.Mode = "Replay";
        status.ReplayFile = _path;
        status.ReplaySpeed = _speed;

        if (!File.Exists(_path))
        {
            status.State = $"Fichier de rejeu introuvable : {_path}";
            status.Log(status.State);
            return;
        }

        status.Connected = true;
        status.State = "Rejeu en cours";
        long totalBytes = new FileInfo(_path).Length;
        using var raw = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using var stream = _path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress)
            : (Stream)raw;
        using var reader = new StreamReader(stream);

        long? firstTs = null;
        var clock = Stopwatch.StartNew();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                var e = MarketFileFormat.Deserialize(line);
                if (e == null) continue;

                firstTs ??= e.Ts;
                if (_speed > 0)
                {
                    double targetMs = (e.Ts - firstTs.Value) / _speed;
                    double waitMs = targetMs - clock.Elapsed.TotalMilliseconds;
                    if (waitMs > 1) await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct);
                }

                _publish(e);
                status.ReplayProgress = totalBytes > 0 ? Math.Min(1, (double)raw.Position / totalBytes) : 1;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            // Fichier tronqué (enregistrement interrompu brutalement) : on s'arrête au dernier message lisible.
            status.Log($"Fin de fichier illisible : {ex.Message}");
        }

        status.ReplayProgress = 1;
        status.ReplayFinished = true;
        status.State = "Rejeu terminé (dernier état conservé)";
    }

    // En rejeu, seules les données enregistrées sont disponibles : rien à souscrire.
    public void SetWatchedInstruments(IReadOnlyCollection<string> instrumentNames)
    {
    }
}
