// Le format des fichiers enregistrés : un message JSON par ligne, compressé en gzip.
using System.IO.Compression;
using System.Text.Json;
using DeribitPricer.Core.Market;

namespace DeribitPricer.Core.Feeds;

// Format des fichiers enregistrés : un MarketEvent JSON par ligne (JSON Lines),
// compressé en gzip si l'extension est .gz.
public static class MarketFileFormat
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static Stream OpenRead(string path)
    {
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        return path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(stream, CompressionMode.Decompress) : stream;
    }

    public static Stream OpenWrite(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Stream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        return path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(stream, CompressionLevel.Fastest) : stream;
    }

    public static string Serialize(MarketEvent e) => JsonSerializer.Serialize(e, JsonOptions);

    public static MarketEvent? Deserialize(string line) =>
        string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<MarketEvent>(line, JsonOptions);
}
