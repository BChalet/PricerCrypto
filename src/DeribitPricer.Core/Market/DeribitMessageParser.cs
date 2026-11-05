// Lit le JSON de chaque message Deribit et met à jour la mémoire du marché. Le live et le rejeu passent tous les deux par ici.
using System.Globalization;
using System.Text.Json;
using DeribitPricer.Core.Pricing;

namespace DeribitPricer.Core.Market;

// Traduit les messages Deribit (REST ou WebSocket) en mises à jour du store.
// C'est le seul point d'entrée des données : le live et le rejeu passent tous deux par ici.
public static class DeribitMessageParser
{
    // Durée pendant laquelle une cotation issue d'un ticker (complète) est préférée
    // à celle du résumé global (sans grecques ni vols bid/ask).
    public static TimeSpan TickerPriority { get; set; } = TimeSpan.FromSeconds(15);

    public static void Apply(MarketEvent e, MarketDataStore store)
    {
        store.AdvanceClock(e.TimestampUtc);
        Interlocked.Increment(ref store.Status.EventCount);

        switch (e.Name)
        {
            case "public/get_instruments":
                ApplyInstruments(e, store);
                break;
            case "public/get_book_summary_by_currency":
                ApplyBookSummary(e, store);
                break;
            case "public/ticker":
                ApplyTicker(e.Data, store);
                break;
            case "public/get_index_price":
                ApplyIndexPrice(e, store);
                break;
            case "public/get_delivery_prices":
                ApplyDeliveryPrices(e, store);
                break;
            default:
                if (e.Name.StartsWith("ticker.", StringComparison.Ordinal))
                    ApplyTicker(e.Data, store);
                else if (e.Name.StartsWith("deribit_price_index.", StringComparison.Ordinal))
                    ApplyIndexNotification(e.Data, store);
                break;
        }
    }

    private static void ApplyInstruments(MarketEvent e, MarketDataStore store)
    {
        if (e.Data.ValueKind != JsonValueKind.Array) return;
        var currency = e.GetParam("currency");
        var kindParam = e.GetParam("kind");
        var parsed = new List<Instrument>();

        foreach (var item in e.Data.EnumerateArray())
        {
            if (item.TryGetProperty("is_active", out var active) && active.ValueKind == JsonValueKind.False) continue;
            var kind = GetString(item, "kind");
            if (kind != "option" && kind != "future") continue;

            var name = GetString(item, "instrument_name") ?? "";
            var baseCurrency = GetString(item, "base_currency") ?? currency ?? "";
            var futureType = GetString(item, "future_type") ?? GetString(item, "instrument_type");
            var optionType = GetString(item, "option_type");

            parsed.Add(new Instrument
            {
                Name = name,
                Currency = baseCurrency,
                Kind = kind == "option" ? InstrumentKind.Option : InstrumentKind.Future,
                OptionType = optionType switch { "call" => OptionType.Call, "put" => OptionType.Put, _ => null },
                Strike = GetDouble(item, "strike") ?? 0,
                ExpiryUtc = FromUnixMs(GetLong(item, "expiration_timestamp") ?? 0),
                ContractSize = GetDouble(item, "contract_size") ?? 1,
                MinTradeAmount = GetDouble(item, "min_trade_amount") ?? 0.1,
                TickSize = GetDouble(item, "tick_size") ?? 0,
                IsPerpetual = GetString(item, "settlement_period") == "perpetual",
                IsInverse = futureType != "linear"
            });
        }

        if (currency != null && kindParam is "option" or "future")
            store.ReplaceInstruments(currency, kindParam == "option" ? InstrumentKind.Option : InstrumentKind.Future, parsed);
        else
            foreach (var instrument in parsed) store.UpsertInstrument(instrument);
    }

    private static void ApplyBookSummary(MarketEvent e, MarketDataStore store)
    {
        if (e.Data.ValueKind != JsonValueKind.Array) return;
        foreach (var item in e.Data.EnumerateArray())
        {
            var name = GetString(item, "instrument_name");
            if (name == null) continue;
            var ts = FromUnixMs(GetLong(item, "creation_timestamp") ?? e.Ts);

            // Si un ticker complet récent existe, on le garde (il est plus riche et plus frais).
            var existing = store.GetQuote(name);
            if (existing is { Source: QuoteSource.Ticker } && ts - existing.TimestampUtc < TickerPriority) continue;

            store.SetQuote(new MarketQuote
            {
                InstrumentName = name,
                TimestampUtc = ts,
                Bid = Positive(GetDouble(item, "bid_price")),
                Ask = Positive(GetDouble(item, "ask_price")),
                Mark = GetDouble(item, "mark_price"),
                MarkIv = Positive(GetDouble(item, "mark_iv")),
                UnderlyingPrice = GetDouble(item, "underlying_price") ?? GetDouble(item, "mark_price") ?? 0,
                IndexPrice = Positive(GetDouble(item, "estimated_delivery_price")),
                Source = QuoteSource.Summary
            });
        }
    }

    private static void ApplyTicker(JsonElement data, MarketDataStore store)
    {
        var name = GetString(data, "instrument_name");
        if (name == null) return;

        double? bid = BestPrice(data, "best_bid_price", "best_bid_amount");
        double? ask = BestPrice(data, "best_ask_price", "best_ask_amount");
        Greeks? greeks = null;
        if (data.TryGetProperty("greeks", out var g) && g.ValueKind == JsonValueKind.Object)
            greeks = new Greeks(GetDouble(g, "delta") ?? 0, GetDouble(g, "gamma") ?? 0,
                GetDouble(g, "vega") ?? 0, GetDouble(g, "theta") ?? 0);

        var mark = GetDouble(data, "mark_price");
        store.SetQuote(new MarketQuote
        {
            InstrumentName = name,
            TimestampUtc = FromUnixMs(GetLong(data, "timestamp") ?? 0),
            Bid = bid,
            Ask = ask,
            Mark = mark,
            MarkIv = Positive(GetDouble(data, "mark_iv")),
            // Deribit renvoie bid_iv = 0 quand il n'y a pas de bid.
            BidIv = bid.HasValue ? Positive(GetDouble(data, "bid_iv")) : null,
            AskIv = ask.HasValue ? Positive(GetDouble(data, "ask_iv")) : null,
            UnderlyingPrice = GetDouble(data, "underlying_price") ?? mark ?? 0,
            IndexPrice = Positive(GetDouble(data, "index_price")),
            DeribitGreeks = greeks,
            Source = QuoteSource.Ticker
        });
    }

    private static void ApplyIndexPrice(MarketEvent e, MarketDataStore store)
    {
        var currency = IndexNames.CurrencyOf(e.GetParam("index_name") ?? "");
        var price = GetDouble(e.Data, "index_price");
        if (currency != null && price is > 0) store.SetIndex(currency, price.Value);
    }

    private static void ApplyIndexNotification(JsonElement data, MarketDataStore store)
    {
        var currency = IndexNames.CurrencyOf(GetString(data, "index_name") ?? "");
        var price = GetDouble(data, "price");
        if (currency != null && price is > 0) store.SetIndex(currency, price.Value);
    }

    private static void ApplyDeliveryPrices(MarketEvent e, MarketDataStore store)
    {
        var currency = IndexNames.CurrencyOf(e.GetParam("index_name") ?? "");
        if (currency == null || !e.Data.TryGetProperty("data", out var list) || list.ValueKind != JsonValueKind.Array) return;
        foreach (var item in list.EnumerateArray())
        {
            var dateText = GetString(item, "date");
            var price = GetDouble(item, "delivery_price");
            if (dateText != null && price is > 0 &&
                DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                store.SetDeliveryPrice(currency, date, price.Value);
        }
    }

    // ---------- Lecture JSON tolérante (champs absents ou null) ----------

    private static double? BestPrice(JsonElement data, string priceField, string amountField)
    {
        var price = GetDouble(data, priceField);
        var amount = GetDouble(data, amountField);
        return price is > 0 && (amount is null || amount > 0) ? price : null;
    }

    private static double? Positive(double? value) => value is > 0 ? value : null;

    public static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public static double? GetDouble(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;

    public static long? GetLong(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? (long)v.GetDouble() : null;

    public static DateTime FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
