// Fait le lien entre une devise et son indice Deribit (BTC donne btc_usd).
namespace DeribitPricer.Core.Market;

// Correspondance devise / nom d'indice Deribit (BTC ↔ btc_usd).
public static class IndexNames
{
    public static string ForCurrency(string currency) => $"{currency.ToLowerInvariant()}_usd";

    public static string? CurrencyOf(string indexName)
    {
        int i = indexName.IndexOf('_');
        return i > 0 ? indexName[..i].ToUpperInvariant() : null;
    }
}
