// Un message reçu de Deribit, tel quel. C'est ce qu'on enregistre dans le fichier et ce qu'on relit en rejeu.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeribitPricer.Core.Market;

// Message brut reçu de Deribit, horodaté à la réception. C'est l'unité enregistrée puis rejouée :
// en live comme en rejeu, le store est alimenté exclusivement par ces événements.
// Ts : Horodatage de réception (ms Unix).
// Src : "rest" ou "ws".
// Name : Méthode REST (public/get_instruments...) ou canal WebSocket (ticker.BTC-...).
// Params : Paramètres de la requête REST (null pour le WebSocket).
// Data : Champ result (REST) ou params.data (notification WebSocket).
public sealed record MarketEvent(
    [property: JsonPropertyName("ts")] long Ts,
    [property: JsonPropertyName("src")] string Src,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("params")] JsonElement? Params,
    [property: JsonPropertyName("data")] JsonElement Data)
{
    [JsonIgnore]
    public DateTime TimestampUtc => DateTimeOffset.FromUnixTimeMilliseconds(Ts).UtcDateTime;

    public string? GetParam(string name) =>
        Params is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) ? v.ToString() : null;
}
