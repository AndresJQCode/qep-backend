using BuildingBlocks.Domain;

namespace Modules.Integrations.Domain;

/// <summary>
/// Regla de negocio de Integrations: <c>ApiExceptionHandler</c> la responde 422 con su código. Si el
/// error es de un campo (spec 2026-10-08, «Códigos de error»), lleva <see cref="FieldErrors"/>.
/// </summary>
public sealed class IntegrationsDomainException : DomainException, IHasFieldErrors
{
    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    public IntegrationsDomainException(
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
        : base(code, message) =>
        FieldErrors = fieldErrors ?? NoFieldErrors;

    /// <summary>Para traducir una falla de base (el índice único del nombre) sin perder la original.</summary>
    public IntegrationsDomainException(string code, string message, Exception innerException)
        : base(code, message, innerException) =>
        FieldErrors = NoFieldErrors;

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }
}
