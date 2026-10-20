using BuildingBlocks.Domain;

namespace Modules.Catalog.Domain;

/// <summary>
/// Regla de negocio de Catalog: <c>ApiExceptionHandler</c> la responde 422 con su código. Desde las
/// variantes (Tarea 5) puede marcar campos, como <c>IntegrationsDomainException</c>: una opción, un
/// valor o un código que sólo se pueden revisar leyendo la base (la biblioteca del tenant, los
/// códigos de otros productos) salen con su código **y** el mapa <c>errors</c> por campo
/// (<c>Variants[3].Code</c>), el único 422 que el formulario sabe leer para marcar el input.
/// </summary>
public sealed class CatalogDomainException : DomainException, IHasFieldErrors
{
    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    public CatalogDomainException(
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
        : base(code, message) =>
        FieldErrors = fieldErrors ?? NoFieldErrors;

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }
}
