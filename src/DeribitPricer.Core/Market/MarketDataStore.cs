// La mémoire du marché : tous les instruments, les dernières cotations, les indices et les prix de règlement. Tout le monde lit ici.
using System.Collections.Concurrent;
using DeribitPricer.Core.Pricing;

namespace DeribitPricer.Core.Market;

// État courant du marché : instruments, dernières cotations, indices, prix de règlement.
// Alimenté par le flux (live ou rejeu), lu par le pricer, le book et l'interface.
// Thread-safe : les cotations sont des records immuables remplacés atomiquement.
public sealed class MarketDataStore
{
    private readonly ConcurrentDictionary<string, Instrument> _instruments = new();
    private readonly ConcurrentDictionary<string, MarketQuote> _quotes = new();
    private readonly ConcurrentDictionary<string, double> _indexPrices = new();
    private readonly ConcurrentDictionary<(string Currency, DateOnly Date), double> _deliveryPrices = new();
    private readonly ConcurrentDictionary<(string Currency, DateTime Expiry), (DateTime Ts, double Price)> _forwards = new();
    private long _lastEventTicks;
    private long _version;

    public FeedStatus Status { get; } = new();

    // En live l'horloge est l'heure système ; en rejeu c'est l'horodatage du dernier message rejoué.
    public bool UseWallClock { get; set; } = true;

    public DateTime Now
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastEventTicks);
            return UseWallClock || ticks == 0 ? DateTime.UtcNow : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    public DateTime? LastEventUtc
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastEventTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    // Compteur incrémenté à chaque modification (utile pour savoir si un recalcul est nécessaire).
    public long Version => Interlocked.Read(ref _version);

    public void AdvanceClock(DateTime utc)
    {
        long ticks = utc.Ticks;
        long current;
        do
        {
            current = Interlocked.Read(ref _lastEventTicks);
            if (ticks <= current) return;
        } while (Interlocked.CompareExchange(ref _lastEventTicks, ticks, current) != current);
    }

    private void Touch() => Interlocked.Increment(ref _version);

    // ---------- Instruments ----------

    // Remplace la liste des instruments d'une devise et d'un type (les échus disparaissent).
    public void ReplaceInstruments(string currency, InstrumentKind kind, IEnumerable<Instrument> instruments)
    {
        var list = instruments.ToList();
        var names = list.Select(i => i.Name).ToHashSet();
        foreach (var old in _instruments.Values.Where(i => i.Currency == currency && i.Kind == kind && !names.Contains(i.Name)))
            _instruments.TryRemove(old.Name, out _);
        foreach (var instrument in list)
            _instruments[instrument.Name] = instrument;
        Touch();
    }

    public void UpsertInstrument(Instrument instrument)
    {
        _instruments[instrument.Name] = instrument;
        Touch();
    }

    public Instrument? GetInstrument(string name) => _instruments.TryGetValue(name, out var i) ? i : null;

    public IReadOnlyList<string> Currencies =>
        _instruments.Values.Select(i => i.Currency).Distinct().OrderBy(c => c).ToList();

    public IReadOnlyList<DateTime> GetOptionExpiries(string currency) =>
        _instruments.Values.Where(i => i.Currency == currency && i.IsOption)
            .Select(i => i.ExpiryUtc).Distinct().OrderBy(e => e).ToList();

    public IReadOnlyList<Instrument> GetOptions(string currency, DateTime expiry) =>
        _instruments.Values.Where(i => i.Currency == currency && i.IsOption && i.ExpiryUtc == expiry)
            .OrderBy(i => i.Strike).ThenBy(i => i.OptionType).ToList();

    public IReadOnlyList<Instrument> GetAllOptions(string currency) =>
        _instruments.Values.Where(i => i.Currency == currency && i.IsOption)
            .OrderBy(i => i.ExpiryUtc).ThenBy(i => i.Strike).ThenBy(i => i.OptionType).ToList();

    public IReadOnlyList<double> GetStrikes(string currency, DateTime expiry, OptionType? type = null) =>
        _instruments.Values.Where(i => i.Currency == currency && i.IsOption && i.ExpiryUtc == expiry
                                       && (type == null || i.OptionType == type))
            .Select(i => i.Strike).Distinct().OrderBy(s => s).ToList();

    public Instrument? FindOption(string currency, DateTime expiry, double strike, OptionType type) =>
        _instruments.Values.FirstOrDefault(i => i.Currency == currency && i.IsOption && i.ExpiryUtc == expiry
                                                && i.OptionType == type && Math.Abs(i.Strike - strike) < 1e-9);

    // Futures de la devise, triés par échéance (le perpétuel en dernier).
    public IReadOnlyList<Instrument> GetFutures(string currency) =>
        _instruments.Values.Where(i => i.Currency == currency && i.Kind == InstrumentKind.Future)
            .OrderBy(i => i.IsPerpetual).ThenBy(i => i.ExpiryUtc).ToList();

    // ---------- Cotations ----------

    public MarketQuote? GetQuote(string instrumentName) =>
        _quotes.TryGetValue(instrumentName, out var q) ? q : null;

    public void SetQuote(MarketQuote quote)
    {
        _quotes[quote.InstrumentName] = quote;

        // Mémorise le dernier future connu de chaque échéance (underlying_price des options).
        var instrument = GetInstrument(quote.InstrumentName);
        if (instrument is { IsOption: true } && quote.UnderlyingPrice > 0)
        {
            var key = (instrument.Currency, instrument.ExpiryUtc);
            _forwards.AddOrUpdate(key, (quote.TimestampUtc, quote.UnderlyingPrice),
                (_, old) => quote.TimestampUtc >= old.Ts ? (quote.TimestampUtc, quote.UnderlyingPrice) : old);
        }
        if (quote.IndexPrice is > 0 && instrument != null)
            _indexPrices[instrument.Currency] = quote.IndexPrice.Value;
        Touch();
    }

    // Future de l'échéance : underlying_price le plus récent des options de cette échéance,
    // à défaut le future Deribit de même échéance, à défaut l'indice.
    public double? GetForward(string currency, DateTime expiry)
    {
        if (_forwards.TryGetValue((currency, expiry), out var fwd)) return fwd.Price;
        var future = GetFutures(currency).FirstOrDefault(f => !f.IsPerpetual && f.ExpiryUtc == expiry);
        if (future != null && GetQuote(future.Name) is { } fq)
        {
            var price = fq.Mark ?? fq.Mid;
            if (price is > 0) return price;
        }
        return GetIndex(currency);
    }

    // ---------- Indices et règlements ----------

    public double? GetIndex(string currency) => _indexPrices.TryGetValue(currency, out var p) ? p : null;

    public void SetIndex(string currency, double price)
    {
        _indexPrices[currency] = price;
        Touch();
    }

    public void SetDeliveryPrice(string currency, DateOnly date, double price)
    {
        _deliveryPrices[(currency, date)] = price;
        Touch();
    }

    public double? GetDeliveryPrice(string currency, DateOnly date) =>
        _deliveryPrices.TryGetValue((currency, date), out var p) ? p : null;
}
