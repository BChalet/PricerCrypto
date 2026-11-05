// Fabrique un faux enregistrement au format Deribit pour tester l'outil sans connexion. Ne tourne pas avec l'appli.
using System.Globalization;
using System.Text.Json;
using DeribitPricer.Core.Feeds;
using DeribitPricer.Core.Market;
using DeribitPricer.Core.Pricing;

// Génère un fichier de rejeu SIMULÉ au format exact des messages Deribit (mêmes méthodes, mêmes canaux,
// mêmes champs). Il sert à tester l'outil sans connexion. Pour la démonstration, préférez un vrai
// enregistrement (mode Live avec "Record": true).
//
// Usage : dotnet run --project tools/DeribitPricer.SampleGenerator -- [fichier de sortie] [minutes]

var output = args.Length > 0 ? args[0] : "data/replay/sample-btc.jsonl.gz";
int minutes = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 8;

var start = new DateTime(2026, 10, 14, 7, 56, 0, DateTimeKind.Utc);  // une échéance tombe à 08:00 pendant le rejeu
var rng = new Random(42);
double spot = 112_000;
const double basis = 0.05;          // base annuelle future / spot
const double realizedVol = 0.50;

DateTime Exp(int y, int m, int d) => new(y, m, d, 8, 0, 0, DateTimeKind.Utc);
var optionExpiries = new (DateTime Expiry, double Step, double RangePct)[]
{
    (Exp(2026, 10, 14), 1000, 5), (Exp(2026, 10, 15), 1000, 8), (Exp(2026, 10, 16), 2000, 12),
    (Exp(2026, 10, 23), 2000, 15), (Exp(2026, 10, 30), 5000, 30), (Exp(2026, 11, 27), 5000, 35),
    (Exp(2026, 12, 25), 10000, 50), (Exp(2027, 3, 26), 10000, 60)
};
var futureExpiries = new[] { Exp(2026, 10, 16), Exp(2026, 10, 23), Exp(2026, 10, 30), Exp(2026, 11, 27), Exp(2026, 12, 25), Exp(2027, 3, 26) };
var watchedExpiries = new[] { Exp(2026, 10, 16), Exp(2026, 10, 23), Exp(2026, 10, 30), Exp(2026, 11, 27) };

string Code(DateTime e) => Instrument.FormatExpiry(e);
long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();

// Instruments
var options = new List<(string Name, DateTime Expiry, double Strike, OptionType Type)>();
foreach (var (expiry, step, range) in optionExpiries)
{
    double lo = Math.Ceiling(spot * (1 - range / 100) / step) * step;
    double hi = Math.Floor(spot * (1 + range / 100) / step) * step;
    for (double k = lo; k <= hi + 1e-9; k += step)
        foreach (var type in new[] { OptionType.Call, OptionType.Put })
            options.Add(($"BTC-{Code(expiry)}-{k:0}-{(type == OptionType.Call ? "C" : "P")}", expiry, k, type));
}
var futures = futureExpiries.Select(e => (Name: $"BTC-{Code(e)}", Expiry: e, Perpetual: false))
    .Append((Name: "BTC-PERPETUAL", Expiry: new DateTime(3000, 1, 1, 8, 0, 0, DateTimeKind.Utc), Perpetual: true)).ToList();

double Forward(DateTime expiry, DateTime now, double s) =>
    s * (1 + basis * Black76.YearFraction(expiry, now));

double SmileVol(DateTime expiry, DateTime now, double f, double k)
{
    double t = Math.Max(Black76.YearFraction(expiry, now), 1e-4);
    double atm = 0.42 + 0.06 * Math.Sqrt(t);
    double m = Math.Log(k / f);
    return Math.Max(0.15, atm - 0.10 * m + 0.9 * m * m / Math.Max(Math.Sqrt(t), 0.15));
}

double RoundDown(double x, double tick) => Math.Floor(x / tick + 1e-9) * tick;
double RoundUp(double x, double tick) => Math.Ceiling(x / tick - 1e-9) * tick;

var events = new List<MarketEvent>();
void Rest(DateTime t, string method, object parameters, object result) =>
    events.Add(new MarketEvent(Ms(t), "rest", method, JsonSerializer.SerializeToElement(parameters), JsonSerializer.SerializeToElement(result)));
void Ws(DateTime t, string channel, object data) =>
    events.Add(new MarketEvent(Ms(t), "ws", channel, null, JsonSerializer.SerializeToElement(data)));

object InstrumentsResult(DateTime now) => options.Where(o => o.Expiry > now).Select(o => new Dictionary<string, object?>
{
    ["tick_size"] = 0.0001, ["taker_commission"] = 0.0003, ["strike"] = o.Strike, ["settlement_period"] = "day",
    ["settlement_currency"] = "BTC", ["quote_currency"] = "BTC", ["price_index"] = "btc_usd",
    ["option_type"] = o.Type == OptionType.Call ? "call" : "put", ["min_trade_amount"] = 0.1, ["maker_commission"] = 0.0003,
    ["kind"] = "option", ["is_active"] = true, ["instrument_name"] = o.Name, ["expiration_timestamp"] = Ms(o.Expiry),
    ["creation_timestamp"] = Ms(start.AddDays(-30)), ["counter_currency"] = "USD", ["contract_size"] = 1.0, ["base_currency"] = "BTC"
}).ToList();

object FuturesResult() => futures.Select(f => new Dictionary<string, object?>
{
    ["tick_size"] = 2.5, ["strike"] = null, ["settlement_period"] = f.Perpetual ? "perpetual" : "week",
    ["settlement_currency"] = "BTC", ["quote_currency"] = "USD", ["price_index"] = "btc_usd", ["min_trade_amount"] = 10.0,
    ["kind"] = "future", ["future_type"] = "reversed", ["instrument_type"] = "reversed", ["is_active"] = true,
    ["instrument_name"] = f.Name, ["expiration_timestamp"] = Ms(f.Expiry), ["contract_size"] = 10.0, ["base_currency"] = "BTC",
    ["counter_currency"] = "USD"
}).ToList();

// Une cotation d'option : mark au smile, bid/ask à ±1,5 point de vol arrondis au tick.
(double Mark, double MarkIv, double? Bid, double? Ask, double? BidIv, double? AskIv, double F, Greeks G) QuoteOption(
    (string Name, DateTime Expiry, double Strike, OptionType Type) o, DateTime now, double s)
{
    double f = Forward(o.Expiry, now, s);
    double t = Black76.YearFraction(o.Expiry, now);
    double vol = SmileVol(o.Expiry, now, f, o.Strike);
    double mark = Black76.PriceCoin(o.Type, f, o.Strike, vol, t);
    double bidVol = vol - 0.015, askVol = vol + 0.015;
    double bid = RoundDown(Black76.PriceCoin(o.Type, f, o.Strike, bidVol, t), 0.0001);
    double ask = Math.Max(RoundUp(Black76.PriceCoin(o.Type, f, o.Strike, askVol, t), 0.0001), 0.0001);
    double? b = bid >= 0.0001 ? bid : null;
    double? a = ask;
    // Quelques options illiquides sans ask, pour illustrer « non traitable ».
    if (Math.Abs(Math.Log(o.Strike / f)) > 0.25 && StableHash(o.Name) % 7 == 0) a = null;
    double? bidIv = b.HasValue ? ImpliedVolSolver.SolveFromCoinPrice(o.Type, b.Value, f, o.Strike, t).Vol * 100 : null;
    double? askIv = a.HasValue ? ImpliedVolSolver.SolveFromCoinPrice(o.Type, a.Value, f, o.Strike, t).Vol * 100 : null;
    return (mark, vol * 100, b, a, bidIv, askIv, f, Black76.ComputeGreeks(o.Type, f, o.Strike, vol, t));
}

double R(double x, int digits) => Math.Round(x, digits);

var watched = new List<(string Name, DateTime Expiry, double Strike, OptionType Type)>();
foreach (var expiry in watchedExpiries)
{
    double f0 = Forward(expiry, start, spot);
    var strikes = options.Where(o => o.Expiry == expiry).Select(o => o.Strike).Distinct().OrderBy(k => Math.Abs(k - f0)).Take(7).ToHashSet();
    watched.AddRange(options.Where(o => o.Expiry == expiry && strikes.Contains(o.Strike)));
}

// Données de référence initiales
Rest(start, "public/get_instruments", new { currency = "BTC", kind = "option", expired = "false" }, InstrumentsResult(start));
Rest(start, "public/get_instruments", new { currency = "BTC", kind = "future", expired = "false" }, FuturesResult());
Rest(start, "public/get_index_price", new { index_name = "btc_usd" }, new { index_price = R(spot, 2), estimated_delivery_price = R(spot, 2) });
var pastDeliveries = Enumerable.Range(1, 10).Select(d => new { date = start.Date.AddDays(-d).ToString("yyyy-MM-dd"), delivery_price = R(spot * (1 + 0.01 * Math.Sin(d)), 2) }).ToList();
Rest(start, "public/get_delivery_prices", new { index_name = "btc_usd", offset = "0", count = "10" }, new { data = pastDeliveries, records_total = 2000 });

double deliveryPrice = 0;
int totalSeconds = minutes * 60;
for (int sec = 0; sec <= totalSeconds; sec++)
{
    var now = start.AddSeconds(sec);
    if (sec > 0) spot *= Math.Exp(realizedVol * Math.Sqrt(1.0 / (365 * 86400)) * Normal(rng) * 6);  // un peu plus agité que nature
    long ts = Ms(now);

    if (deliveryPrice == 0 && now >= Exp(2026, 10, 14)) deliveryPrice = R(spot, 2);

    Ws(now, "deribit_price_index.btc_usd", new { index_name = "btc_usd", price = R(spot, 2), timestamp = ts });

    foreach (var f in futures)
    {
        double fp = f.Perpetual ? spot * 1.0001 : Forward(f.Expiry, now, spot);
        double mark = R(fp, 2);
        Ws(now, $"ticker.{f.Name}.100ms", new
        {
            timestamp = ts, state = "open", instrument_name = f.Name, index_price = R(spot, 2), mark_price = mark,
            best_bid_price = RoundDown(fp - 2.5, 2.5), best_bid_amount = 50000.0,
            best_ask_price = RoundUp(fp + 2.5, 2.5), best_ask_amount = 40000.0, last_price = mark,
            estimated_delivery_price = R(spot, 2), open_interest = 1e8
        });
    }

    if (sec % 2 == 0)
    {
        foreach (var o in watched)
        {
            var q = QuoteOption(o, now, spot);
            Ws(now, $"ticker.{o.Name}.100ms", new
            {
                timestamp = ts, state = "open", instrument_name = o.Name, underlying_price = R(q.F, 2),
                underlying_index = $"BTC-{Code(o.Expiry)}", index_price = R(spot, 2), estimated_delivery_price = R(spot, 2),
                mark_price = R(q.Mark, 8), mark_iv = R(q.MarkIv, 2),
                best_bid_price = q.Bid ?? 0, best_bid_amount = q.Bid.HasValue ? 12.5 : 0,
                best_ask_price = q.Ask ?? 0, best_ask_amount = q.Ask.HasValue ? 8.3 : 0,
                bid_iv = q.BidIv.HasValue ? R(q.BidIv.Value, 2) : 0, ask_iv = q.AskIv.HasValue ? R(q.AskIv.Value, 2) : 0,
                interest_rate = 0.0, open_interest = 120.0, last_price = (double?)null,
                greeks = new { delta = R(q.G.Delta, 5), gamma = R(q.G.Gamma, 5), vega = R(q.G.Vega, 5), theta = R(q.G.Theta, 5), rho = 0.0 }
            });
        }
    }

    if (sec % 5 == 0)
    {
        var summary = options.Where(o => o.Expiry > now).Select(o =>
        {
            var q = QuoteOption(o, now, spot);
            return new Dictionary<string, object?>
            {
                ["volume_usd"] = 0.0, ["volume"] = 0.0, ["underlying_price"] = R(q.F, 2), ["underlying_index"] = $"BTC-{Code(o.Expiry)}",
                ["quote_currency"] = "BTC", ["price_change"] = null, ["open_interest"] = 50.0,
                ["mid_price"] = q.Bid.HasValue && q.Ask.HasValue ? (q.Bid + q.Ask) / 2 : null,
                ["mark_price"] = R(q.Mark, 8), ["mark_iv"] = R(q.MarkIv, 2), ["low"] = null, ["last"] = null, ["interest_rate"] = 0.0,
                ["instrument_name"] = o.Name, ["high"] = null, ["estimated_delivery_price"] = R(spot, 2),
                ["creation_timestamp"] = ts, ["bid_price"] = q.Bid, ["base_currency"] = "BTC", ["ask_price"] = q.Ask
            };
        }).ToList();
        Rest(now, "public/get_book_summary_by_currency", new { currency = "BTC", kind = "option" }, summary);
    }

    // Après l'échéance de 08:00 : rafraîchissement des référentiels puis publication du prix de règlement.
    if (now == Exp(2026, 10, 14).AddSeconds(30))
        Rest(now, "public/get_instruments", new { currency = "BTC", kind = "option", expired = "false" }, InstrumentsResult(now));
    if (now == Exp(2026, 10, 14).AddSeconds(60))
    {
        var deliveries = pastDeliveries.Prepend(new { date = "2026-10-14", delivery_price = deliveryPrice }).Take(10).ToList();
        Rest(now, "public/get_delivery_prices", new { index_name = "btc_usd", offset = "0", count = "10" }, new { data = deliveries, records_total = 2001 });
    }
}

await using (var stream = MarketFileFormat.OpenWrite(output))
await using (var writer = new StreamWriter(stream))
{
    foreach (var e in events) await writer.WriteLineAsync(MarketFileFormat.Serialize(e));
}
Console.WriteLine($"{events.Count} messages, {options.Count} options, {futures.Count} futures -> {Path.GetFullPath(output)} ({new FileInfo(output).Length / 1024} Ko)");

static double Normal(Random rng)
{
    double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
    return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
}

// string.GetHashCode() change à chaque exécution en .NET : hachage stable pour un fichier reproductible.
static int StableHash(string s) => s.Aggregate(17, (h, c) => unchecked(h * 31 + c)) & int.MaxValue;
