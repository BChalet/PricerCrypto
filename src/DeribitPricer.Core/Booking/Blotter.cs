// Le blotter : la liste des trades, sauvegardée dans un fichier JSON à chaque nouveau trade.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeribitPricer.Core.Booking;

// Registre des trades, sauvegardé en JSON à chaque ajout (les trades survivent à un redémarrage).
public sealed class Blotter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _lock = new();
    private readonly List<Trade> _trades = new();
    private readonly string? _filePath;

    public Blotter(string? filePath = null)
    {
        _filePath = filePath;
        if (filePath != null && File.Exists(filePath))
        {
            var loaded = JsonSerializer.Deserialize<List<Trade>>(File.ReadAllText(filePath), JsonOptions);
            if (loaded != null) _trades.AddRange(loaded);
        }
    }

    public event Action? Changed;

    public IReadOnlyList<Trade> Trades
    {
        get { lock (_lock) return _trades.ToList(); }
    }

    public Trade? Find(string id)
    {
        lock (_lock) return _trades.FirstOrDefault(t => t.Id == id);
    }

    // Quantité de structure encore ouverte d'un trade d'origine (après débouclages).
    public double OpenQuantity(string originalId)
    {
        lock (_lock)
        {
            var original = _trades.FirstOrDefault(t => t.Id == originalId);
            if (original == null) return 0;
            double unwound = _trades.Where(t => t.ParentTradeId == originalId).Sum(t => t.Quantity);
            return Math.Round(original.Quantity - unwound, 10);
        }
    }

    public Trade Add(Trade trade)
    {
        lock (_lock)
        {
            int next = _trades.Count == 0 ? 1 : _trades.Max(t => int.TryParse(t.Id.TrimStart('T'), out var n) ? n : 0) + 1;
            trade.Id = $"T{next:D5}";
            _trades.Add(trade);
            Save();
        }
        Changed?.Invoke();
        return trade;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _trades.Clear();
            Save();
        }
        Changed?.Invoke();
    }

    private void Save()
    {
        if (_filePath == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_filePath))!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_trades, JsonOptions));
    }
}
