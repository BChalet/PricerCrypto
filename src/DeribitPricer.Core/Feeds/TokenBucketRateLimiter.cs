// Le limiteur de fréquence : chaque requête à Deribit doit attendre son tour, 5 par seconde maximum par défaut.
using System.Diagnostics;

namespace DeribitPricer.Core.Feeds;

// Limiteur de fréquence « seau à jetons » : chaque requête consomme un jeton,
// le seau se remplit à débit constant et contient au plus burst jetons.
// Toutes les requêtes vers Deribit (REST et WebSocket) passent par ici.
public sealed class TokenBucketRateLimiter
{
    private readonly double _ratePerSecond;
    private readonly double _capacity;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _tokens;
    private double _lastRefillSeconds;

    public TokenBucketRateLimiter(double ratePerSecond, int burst)
    {
        if (ratePerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ratePerSecond));
        _ratePerSecond = ratePerSecond;
        _capacity = Math.Max(1, burst);
        _tokens = _capacity;
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            while (true)
            {
                Refill();
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return;
                }
                double waitSeconds = (1 - _tokens) / _ratePerSecond;
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Refill()
    {
        double now = _clock.Elapsed.TotalSeconds;
        _tokens = Math.Min(_capacity, _tokens + (now - _lastRefillSeconds) * _ratePerSecond);
        _lastRefillSeconds = now;
    }
}
