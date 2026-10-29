using BuildingBlocks.Domain;

namespace Modules.Billing.Domain;

public sealed class BillingDomainException(string code, string message)
    : DomainException(code, message);
