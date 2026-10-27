using BuildingBlocks.Domain;

namespace Modules.Quotations.Domain;

public sealed class QuotationsDomainException : DomainException, IHasFieldErrors
{
    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    public QuotationsDomainException(string code, string message)
        : base(code, message) =>
        FieldErrors = NoFieldErrors;

    /// <summary>Con el mapa <c>errors</c> del 422, como <c>CustomersDomainException</c> (spec 2026-10-26).</summary>
    public QuotationsDomainException(
        string code,
        string message,
        IReadOnlyDictionary<string, string[]> fieldErrors)
        : base(code, message) =>
        FieldErrors = fieldErrors;

    /// <summary>Ver <see cref="DomainException"/>: lo usa el envio de cotizaciones para
    /// traducir una falla inesperada sin perderla.</summary>
    public QuotationsDomainException(string code, string message, Exception innerException)
        : base(code, message, innerException) =>
        FieldErrors = NoFieldErrors;

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }
}
