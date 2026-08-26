using BuildingBlocks.Domain;

namespace Modules.Quotations.Domain;

public sealed class QuotationsDomainException(string code, string message)
    : DomainException(code, message);
