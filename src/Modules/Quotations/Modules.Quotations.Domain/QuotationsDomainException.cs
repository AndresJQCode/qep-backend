using BuildingBlocks.Domain;

namespace Modules.Quotations.Domain;

public sealed class QuotationsDomainException : DomainException
{
    public QuotationsDomainException(string code, string message)
        : base(code, message)
    {
    }

    /// <summary>Ver <see cref="DomainException"/>: lo usa el envio de cotizaciones para
    /// traducir una falla inesperada sin perderla.</summary>
    public QuotationsDomainException(string code, string message, Exception innerException)
        : base(code, message, innerException)
    {
    }
}
