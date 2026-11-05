// L'état du flux affiché à l'écran : connecté ou pas, mode, progression du rejeu, journal des messages.
namespace DeribitPricer.Core.Market;

// État du flux de données, affiché dans la barre de statut.
public sealed class FeedStatus
{
    private readonly object _lock = new();
    private readonly LinkedList<string> _messages = new();

    public string Mode { get; set; } = "Live";
    public bool Connected { get; set; }
    public string State { get; set; } = "Démarrage";
    public bool Recording { get; set; }
    public string? RecordFile { get; set; }
    public string? ReplayFile { get; set; }
    public double ReplaySpeed { get; set; } = 1;
    public double ReplayProgress { get; set; }
    public bool ReplayFinished { get; set; }
    public long EventCount;

    // Journal des derniers messages (erreurs, reconnexions...).
    public void Log(string message)
    {
        lock (_lock)
        {
            _messages.AddFirst($"{DateTime.UtcNow:HH:mm:ss} {message}");
            while (_messages.Count > 50) _messages.RemoveLast();
        }
    }

    public IReadOnlyList<string> Messages
    {
        get { lock (_lock) return _messages.ToList(); }
    }
}
