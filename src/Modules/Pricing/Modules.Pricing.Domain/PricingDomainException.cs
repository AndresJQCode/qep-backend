using BuildingBlocks.Domain;

namespace Modules.Pricing.Domain;

public sealed class PricingDomainException(string code, string message)
    : DomainException(code, message);
