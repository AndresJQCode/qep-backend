using BuildingBlocks.Domain;

namespace Modules.Wholesale.Domain;

public sealed class WholesaleDomainException(string code, string message)
    : DomainException(code, message);
