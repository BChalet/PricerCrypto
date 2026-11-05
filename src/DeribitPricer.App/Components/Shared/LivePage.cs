// La base de toutes les pages en temps réel : un minuteur recalcule et redessine la page chaque seconde.
using DeribitPricer.App.Services;
using DeribitPricer.Core.Feeds;
using DeribitPricer.Core.Market;
using Microsoft.AspNetCore.Components;

namespace DeribitPricer.App.Components.Shared;

// Base des écrans « temps réel » : un minuteur recalcule l'écran (OnTick) puis le redessine
// toutes les RefreshMilliseconds. Les instruments suivis via Watch() sont libérés à la fermeture.
public abstract class LivePage : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    [Inject] protected MarketDataService Market { get; set; } = default!;
    [Inject] protected AppOptions AppOptions { get; set; } = default!;

    protected MarketDataStore Store => Market.Store;
    protected string WatchOwner { get; } = Guid.NewGuid().ToString("N");
    protected string? TickError { get; private set; }

    protected override void OnInitialized()
    {
        OnStart();
        _ = LoopAsync();
    }

    // Initialisation de l'écran.
    protected virtual void OnStart()
    {
    }

    // Recalcul périodique (cotations, book...).
    protected virtual void OnTick()
    {
    }

    // Demande au flux le ticker complet de ces instruments.
    protected void Watch(IEnumerable<string> instrumentNames) => Market.Watch(WatchOwner, instrumentNames);

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(200, AppOptions.RefreshMilliseconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                await InvokeAsync(() =>
                {
                    try
                    {
                        OnTick();
                        TickError = null;
                    }
                    catch (Exception ex)
                    {
                        TickError = ex.Message;
                    }
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public virtual void Dispose()
    {
        _cts.Cancel();
        Market.Unwatch(WatchOwner);
        GC.SuppressFinalize(this);
    }
}
