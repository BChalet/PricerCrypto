// L'enregistreur : il écrit chaque message reçu dans le fichier, sans ralentir la réception.
using System.Threading.Channels;
using DeribitPricer.Core.Market;

namespace DeribitPricer.Core.Feeds;

// Enregistre dans un fichier tous les messages reçus de Deribit, dans l'ordre de réception.
// L'écriture se fait sur une tâche dédiée pour ne jamais ralentir la réception.
public sealed class MarketRecorder : IAsyncDisposable
{
    private readonly Channel<MarketEvent> _queue = Channel.CreateUnbounded<MarketEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;

    public MarketRecorder(string path)
    {
        FilePath = path;
        _writer = Task.Run(() => WriteLoopAsync(path));
    }

    public string FilePath { get; }

    public void Write(MarketEvent e) => _queue.Writer.TryWrite(e);

    private async Task WriteLoopAsync(string path)
    {
        await using var stream = MarketFileFormat.OpenWrite(path);
        await using var writer = new StreamWriter(stream);
        var lastFlush = DateTime.UtcNow;
        await foreach (var e in _queue.Reader.ReadAllAsync())
        {
            await writer.WriteLineAsync(MarketFileFormat.Serialize(e));
            if (DateTime.UtcNow - lastFlush > TimeSpan.FromSeconds(5))
            {
                await writer.FlushAsync();
                lastFlush = DateTime.UtcNow;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer;
    }
}
