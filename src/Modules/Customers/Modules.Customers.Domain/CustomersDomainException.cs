using BuildingBlocks.Domain;

namespace Modules.Customers.Domain;

/// <summary>
/// Regla de negocio de Customers: <c>ApiExceptionHandler</c> la responde 422 con su código. Desde el tipo de
/// persona (spec 2026-10-26) puede marcar campos con el mapa <c>errors</c>, como <c>CatalogDomainException</c>.
/// </summary>
public sealed class CustomersDomainException : DomainException, IHasFieldErrors
{
    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    public CustomersDomainException(
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
        : base(code, message) =>
        FieldErrors = fieldErrors ?? NoFieldErrors;

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }
}
