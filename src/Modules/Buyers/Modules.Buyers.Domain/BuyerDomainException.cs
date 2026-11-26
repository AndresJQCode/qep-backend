using BuildingBlocks.Domain;

namespace Modules.Buyers.Domain;

public sealed class BuyerDomainException(string code, string message) : DomainException(code, message);
