// Ce que doit savoir faire une source de données, qu'elle soit live ou rejeu.
namespace DeribitPricer.Core.Feeds;

// Source de données de marché : live (Deribit) ou rejeu d'un fichier enregistré.
public interface IMarketDataFeed
{
    // Tourne jusqu'à l'annulation et publie les messages reçus.
    Task RunAsync(CancellationToken ct);

    // Instruments pour lesquels on veut le ticker complet en temps réel
    // (jambes de la structure cotée, positions du book, couvertures).
    void SetWatchedInstruments(IReadOnlyCollection<string> instrumentNames);
}
