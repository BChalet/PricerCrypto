// La dernière cotation connue d'un instrument : bid, ask, mark, vols, future, grecques Deribit.
using DeribitPricer.Core.Pricing;

namespace DeribitPricer.Core.Market;

public enum QuoteSource
{
    // public/get_book_summary_by_currency (toutes les options, sans grecques ni vols bid/ask).
    Summary,
    // Canal ticker en WebSocket (ou public/ticker) : données complètes.
    Ticker
}

// Dernière cotation connue d'un instrument. Pour une option, les prix sont en crypto (BTC/ETH) ;
// pour un future, en USD. Les vols sont en % comme chez Deribit (55.3 = 55,3 %).
// Un champ null signifie « absent » (pas de bid, pas d'ask...).
public sealed record MarketQuote
{
    public required string InstrumentName { get; init; }
    public DateTime TimestampUtc { get; init; }
    public double? Bid { get; init; }
    public double? Ask { get; init; }
    public double? Mark { get; init; }
    public double? MarkIv { get; init; }
    public double? BidIv { get; init; }
    public double? AskIv { get; init; }
    // Future de même échéance (champ underlying_price).
    public double UnderlyingPrice { get; init; }
    public double? IndexPrice { get; init; }
    public Greeks? DeribitGreeks { get; init; }
    public QuoteSource Source { get; init; }

    public double? Mid => Bid.HasValue && Ask.HasValue ? (Bid.Value + Ask.Value) / 2 : null;
}
