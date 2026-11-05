// Les appels HTTP à l'API Deribit (get_instruments, ticker...), en respectant la limite de fréquence.
using System.Net;
using System.Text.Json;
using DeribitPricer.Core.Market;

namespace DeribitPricer.Core.Feeds;

public sealed class DeribitApiException(string message) : Exception(message);

// Client HTTP de l'API publique Deribit (JSON-RPC sur GET). Chaque réponse est renvoyée
// sous forme de MarketEvent pour être appliquée au store et éventuellement enregistrée.
public sealed class DeribitRestClient
{
    private readonly HttpClient _http;
    private readonly TokenBucketRateLimiter _limiter;

    public DeribitRestClient(HttpClient http, TokenBucketRateLimiter limiter)
    {
        _http = http;
        _limiter = limiter;
    }

    public async Task<MarketEvent> CallAsync(string method, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var query = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        var url = query.Length > 0 ? $"{method}?{query}" : method;

        for (int attempt = 1; ; attempt++)
        {
            await _limiter.WaitAsync(ct);
            using var response = await _http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            // Dépassement de la limite de fréquence (HTTP 429 ou code Deribit 10028) : on attend et on réessaie.
            bool tooManyRequests = response.StatusCode == HttpStatusCode.TooManyRequests || body.Contains("\"code\":10028");
            if (tooManyRequests && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                continue;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new DeribitApiException($"{method} : {error}");
            if (!root.TryGetProperty("result", out var result))
                throw new DeribitApiException($"{method} : réponse sans champ result (HTTP {(int)response.StatusCode})");

            var paramsElement = JsonSerializer.SerializeToElement(parameters);
            return new MarketEvent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "rest", method, paramsElement, result.Clone());
        }
    }
}
