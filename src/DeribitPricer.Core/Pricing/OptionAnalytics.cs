// Tout ce qu'on calcule pour une option cotée : prix théorique, écart au mark, vols recalculées comparées à Deribit, grecques.
using DeribitPricer.Core.Market;

namespace DeribitPricer.Core.Pricing;

// Comparaison entre une vol calculée par notre inversion de Black-76 et celle publiée par Deribit (en %).
public sealed record VolComparison(double? ComputedPct, double? DeribitPct, string? Error)
{
    // Seuil de signalement : 0,1 point de vol.
    public const double ThresholdPoints = 0.1;

    public double? DiffPoints => ComputedPct.HasValue && DeribitPct.HasValue ? ComputedPct - DeribitPct : null;
    public bool Flag => DiffPoints is double d && Math.Abs(d) > ThresholdPoints;
}

// Analyse d'une option cotée à partir de sa dernière cotation : prix Black-76 (vol du mark),
// écart au mark Deribit, vols implicites recalculées, grecques.
public sealed record OptionAnalytics
{
    // Seuil de signalement de l'écart au mark : 0,0001 BTC.
    public const double MarkThresholdCoin = 0.0001;

    public required Instrument Instrument { get; init; }
    public required MarketQuote Quote { get; init; }
    public double Forward { get; init; }
    // Maturité en années, mesurée depuis l'horodatage de la cotation.
    public double T { get; init; }
    // Vol du mark Deribit en décimal (0,55).
    public double? Vol { get; init; }
    public double? TheoUsd { get; init; }
    public double? TheoCoin { get; init; }
    public required VolComparison BidVol { get; init; }
    public required VolComparison MarkVol { get; init; }
    public required VolComparison AskVol { get; init; }
    // Grecques unitaires (une option), conventions annexe B.
    public Greeks? Greeks { get; init; }

    public double? MarkDiffCoin => TheoCoin.HasValue && Quote.Mark.HasValue ? TheoCoin - Quote.Mark : null;
    public bool MarkDiffFlag => MarkDiffCoin is double d && Math.Abs(d) > MarkThresholdCoin;
    public Greeks? DeribitGreeks => Quote.DeribitGreeks;
    public OptionType Type => Instrument.OptionType ?? OptionType.Call;

    public static OptionAnalytics? Compute(Instrument instrument, MarketQuote? quote)
    {
        if (quote == null || instrument.OptionType == null || quote.UnderlyingPrice <= 0) return null;

        var type = instrument.OptionType.Value;
        double f = quote.UnderlyingPrice;
        double k = instrument.Strike;
        double t = Black76.YearFraction(instrument.ExpiryUtc, quote.TimestampUtc);
        double? vol = quote.MarkIv / 100;

        double? theoUsd = vol.HasValue ? Black76.PriceUsd(type, f, k, vol.Value, t) : null;
        return new OptionAnalytics
        {
            Instrument = instrument,
            Quote = quote,
            Forward = f,
            T = t,
            Vol = vol,
            TheoUsd = theoUsd,
            TheoCoin = theoUsd / f,
            BidVol = Compare(type, quote.Bid, quote.BidIv, f, k, t, "pas de bid"),
            MarkVol = Compare(type, quote.Mark, quote.MarkIv, f, k, t, "pas de mark"),
            AskVol = Compare(type, quote.Ask, quote.AskIv, f, k, t, "pas d'ask"),
            Greeks = vol.HasValue ? Black76.ComputeGreeks(type, f, k, vol.Value, t) : null
        };
    }

    private static VolComparison Compare(OptionType type, double? priceCoin, double? deribitIv, double f, double k, double t, string missing)
    {
        if (!priceCoin.HasValue) return new VolComparison(null, deribitIv, missing);
        var result = ImpliedVolSolver.SolveFromCoinPrice(type, priceCoin.Value, f, k, t);
        return result.Success
            ? new VolComparison(result.Vol * 100, deribitIv, null)
            : new VolComparison(null, deribitIv, "incalculable : " + result.Error);
    }
}
