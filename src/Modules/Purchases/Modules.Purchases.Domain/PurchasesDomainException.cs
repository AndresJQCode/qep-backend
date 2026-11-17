using BuildingBlocks.Domain;

namespace Modules.Purchases.Domain;

public sealed class PurchasesDomainException(string code, string message)
    : DomainException(code, message);
