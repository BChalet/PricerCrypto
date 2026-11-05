// La tâche de fond qui fait tourner le flux de marché (live ou rejeu) tant que l'appli est ouverte, et qui ferme bien l'enregistrement à l'arrêt.
using DeribitPricer.Core.Feeds;

namespace DeribitPricer.App.Services;

// Fait tourner le flux de marché en tâche de fond pendant toute la vie de l'application.
public sealed class MarketDataHostedService(MarketDataService market, ILogger<MarketDataHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Flux de marché : mode {Mode}", market.Options.Mode);
        try
        {
            await market.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Arrêt du flux de marché");
            market.Store.Status.State = "Flux arrêté : " + ex.Message;
            market.Store.Status.Log(ex.Message);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await market.DisposeAsync();  // termine proprement le fichier d'enregistrement
    }
}
