using BuildingBlocks.Domain;

namespace Modules.Payroll.Domain;

/// <summary>
/// Regla de negocio de Payroll: <c>ApiExceptionHandler</c> la responde 422 con su código. Calco de <c>HrDomainException</c>: puede
/// marcar campos, y entonces sale con su código <b>y</b> el mapa <c>errors</c> por campo (<see cref="IHasFieldErrors"/>).
/// </summary>
public sealed class PayrollDomainException : DomainException, IHasFieldErrors
{
    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    public PayrollDomainException(
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
        : base(code, message) =>
        FieldErrors = fieldErrors ?? NoFieldErrors;

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }
}
