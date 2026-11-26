using BuildingBlocks.Domain;

namespace Modules.Scheduling.Domain;

/// <summary>
/// Regla de negocio de la Agenda: <c>ApiExceptionHandler</c> la responde 422 con su código. Calco de <c>HrDomainException</c>: puede
/// marcar campos, y entonces sale con su código <b>y</b> el mapa <c>errors</c> por campo (<see cref="IHasFieldErrors"/>), el único 422
/// que el formulario sabe leer para marcar el input.
/// </summary>
public sealed class SchedulingDomainException : DomainException, IHasFieldErrors
{
    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    public SchedulingDomainException(
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
        : base(code, message) =>
        FieldErrors = fieldErrors ?? NoFieldErrors;

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }
}
