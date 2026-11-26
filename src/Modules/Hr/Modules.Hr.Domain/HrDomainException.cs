using BuildingBlocks.Domain;

namespace Modules.Hr.Domain;

/// <summary>
/// Regla de negocio de Hr: <c>ApiExceptionHandler</c> la responde 422 con su código. Calco de
/// <c>CatalogDomainException</c>: puede marcar campos, y entonces sale con su código <b>y</b> el mapa <c>errors</c> por campo
/// (<see cref="IHasFieldErrors"/>), el único 422 que el formulario sabe leer para marcar el input
/// (<c>hr.assignment.workplace_other_company</c>).
/// </summary>
public sealed class HrDomainException : DomainException, IHasFieldErrors
{
    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    public HrDomainException(
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
        : base(code, message) =>
        FieldErrors = fieldErrors ?? NoFieldErrors;

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }
}
