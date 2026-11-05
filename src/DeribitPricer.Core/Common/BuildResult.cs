// Un résultat qui peut être un succès ou un refus avec des messages d'erreur à afficher tels quels.
namespace DeribitPricer.Core.Common;

// Résultat d'une opération qui peut être refusée (saisie incohérente, système non résoluble...).
// Les messages d'erreur sont destinés à être affichés tels quels à l'utilisateur.
public sealed class BuildResult<T>
{
    private BuildResult(T? value, IReadOnlyList<string> errors)
    {
        Value = value;
        Errors = errors;
    }

    public T? Value { get; }
    public IReadOnlyList<string> Errors { get; }
    public bool IsSuccess => Errors.Count == 0 && Value is not null;

    public static BuildResult<T> Ok(T value) => new(value, Array.Empty<string>());
    public static BuildResult<T> Fail(params string[] errors) => new(default, errors);
    public static BuildResult<T> Fail(IEnumerable<string> errors) => new(default, errors.ToList());
}
