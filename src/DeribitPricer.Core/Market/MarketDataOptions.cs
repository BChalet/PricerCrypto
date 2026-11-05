// La section MarketData de appsettings.json sous forme de classe : mode, devises, adresses Deribit, limites, enregistrement, rejeu.
namespace DeribitPricer.Core.Market;

// Configuration du flux (section « MarketData » de appsettings.json).
// Le passage live / rejeu se fait ici, sans modifier le code.
public sealed class MarketDataOptions
{
    public const string SectionName = "MarketData";

    // "Live" ou "Replay".
    public string Mode { get; set; } = "Live";
    public List<string> Currencies { get; set; } = ["BTC"];

    public string RestBaseUrl { get; set; } = "https://www.deribit.com/api/v2/";
    public string WebSocketUrl { get; set; } = "wss://www.deribit.com/ws/api/v2";

    // Limite de fréquence des requêtes (seau à jetons) : débit moyen et rafale maximale.
    public double MaxRequestsPerSecond { get; set; } = 5;
    public int MaxBurst { get; set; } = 5;

    // Période de rafraîchissement du résumé de toutes les options (get_book_summary_by_currency).
    public int SummaryPollSeconds { get; set; } = 5;
    // Période de rechargement des référentiels et des prix de règlement.
    public int ReferenceRefreshMinutes { get; set; } = 10;
    // Intervalle des canaux ticker WebSocket (100ms ou agg2).
    public string TickerInterval { get; set; } = "100ms";

    // Enregistrement des messages reçus en live.
    public bool Record { get; set; }
    public string RecordDirectory { get; set; } = "data/recordings";

    // Fichier rejoué en mode Replay (.jsonl ou .jsonl.gz).
    public string ReplayFile { get; set; } = "data/replay/sample-btc.jsonl.gz";
    // 1 = vitesse d'origine, 10 = dix fois plus vite.
    public double ReplaySpeed { get; set; } = 1;

    public bool IsReplay => string.Equals(Mode, "Replay", StringComparison.OrdinalIgnoreCase);
}
