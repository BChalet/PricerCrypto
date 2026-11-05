// Une option ou un future Deribit avec ses caractéristiques : strike, échéance, quantité minimale...
using System.Globalization;
using DeribitPricer.Core.Pricing;

namespace DeribitPricer.Core.Market;

public enum InstrumentKind
{
    Option,
    Future
}

// Caractéristiques d'un instrument Deribit (réponse de public/get_instruments).
public sealed record Instrument
{
    public required string Name { get; init; }
    // Devise de base : BTC, ETH...
    public required string Currency { get; init; }
    public required InstrumentKind Kind { get; init; }
    public OptionType? OptionType { get; init; }
    public double Strike { get; init; }
    public DateTime ExpiryUtc { get; init; }
    // Options : 1 unité de crypto. Futures inverses : montant en USD par contrat (10 pour BTC).
    public double ContractSize { get; init; } = 1;
    // Quantité minimale, qui sert aussi de pas de quantité.
    public double MinTradeAmount { get; init; } = 0.1;
    public double TickSize { get; init; }
    public bool IsPerpetual { get; init; }
    // Future inverse : quantité en USD, réglé en crypto.
    public bool IsInverse { get; init; } = true;

    public bool IsOption => Kind == InstrumentKind.Option;

    // Code d'échéance au format Deribit, par exemple 27DEC26.
    public string ExpiryCode => FormatExpiry(ExpiryUtc);

    public static string FormatExpiry(DateTime expiryUtc) =>
        expiryUtc.ToString("dMMMyy", CultureInfo.InvariantCulture).ToUpperInvariant();

    // Pas de quantité exprimé en unités de sous-jacent. Pour un future inverse, la quantité
    // Deribit est en USD : le pas en crypto vaut alors MinTradeAmount / F.
    public double QuantityStepInCoin(double forward) =>
        Kind == InstrumentKind.Future && IsInverse && forward > 0 ? MinTradeAmount / forward : MinTradeAmount;
}
