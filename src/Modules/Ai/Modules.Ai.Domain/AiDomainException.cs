using BuildingBlocks.Domain;

namespace Modules.Ai.Domain;

/// <summary>Regla de negocio de los agentes: <c>ApiExceptionHandler</c> la responde 422 con su código. Como
/// <c>AutomationsDomainException</c>, puede llevar el mapa <c>errors</c>: publicar con errores (D-A7) manda cada código bajo la ruta del
/// <c>config</c> que lo causó, para que el editor marque el campo.</summary>
public sealed class AiDomainException : DomainException, IHasFieldErrors
{
    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    public AiDomainException(string code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors = null)
        : base(code, message) =>
        FieldErrors = fieldErrors ?? NoFieldErrors;

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }
}
