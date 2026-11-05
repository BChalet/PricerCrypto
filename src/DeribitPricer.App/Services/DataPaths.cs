// Retrouve le dossier du projet pour que les chemins comme data/... marchent quel que soit l'endroit d'où on lance l'appli.
namespace DeribitPricer.App.Services;

// Résout les chemins relatifs de la configuration (data/...) par rapport à la racine de la solution,
// quel que soit le dossier depuis lequel l'application est lancée (VS Code, terminal...).
public static class DataPaths
{
    private static readonly Lazy<string> Root = new(FindRoot);

    public static string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Root.Value, path));

    private static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "DeribitPricer.sln")))
                    return dir.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
