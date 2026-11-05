// Les réglages de l'interface (section App de appsettings.json) : rythme de rafraîchissement, fichiers du blotter.
namespace DeribitPricer.App.Services;

// Configuration de l'interface (section « App » de appsettings.json).
public sealed class AppOptions
{
    public const string SectionName = "App";

    // Période de rafraîchissement des écrans.
    public int RefreshMilliseconds { get; set; } = 1000;
    public string BlotterFile { get; set; } = "data/blotter.json";
    // Blotter séparé en rejeu pour ne pas mélanger démonstration et live.
    public string ReplayBlotterFile { get; set; } = "data/blotter-replay.json";
    // Repartir d'un blotter vide à chaque rejeu (le rejeu recommence au début du fichier).
    public bool ResetReplayBlotterOnStart { get; set; } = true;
}
