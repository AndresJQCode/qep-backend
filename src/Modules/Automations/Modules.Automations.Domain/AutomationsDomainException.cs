using BuildingBlocks.Domain;

namespace Modules.Automations.Domain;

/// <summary>Regla de negocio de los chatbots: <c>ApiExceptionHandler</c> la responde 422 con su código. Como
/// <c>CatalogDomainException</c>, puede llevar el mapa <c>errors</c> por campo: publicar con errores (D-C3) manda los problemas del
/// flujo bajo <c>flow</c>.</summary>
public sealed class AutomationsDomainException : DomainException, IHasFieldErrors
{
    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    public AutomationsDomainException(string code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors = null)
        : base(code, message) =>
        FieldErrors = fieldErrors ?? NoFieldErrors;

    /// <summary>Cuando el 422 envuelve una falla de la base: la causa queda en <c>platform.request_failures</c>.</summary>
    public AutomationsDomainException(string code, string message, Exception innerException)
        : base(code, message, innerException) =>
        FieldErrors = NoFieldErrors;

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }
}
