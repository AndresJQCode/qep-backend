using BuildingBlocks.Domain;

namespace Modules.Tenancy.Domain;

public sealed class TenantDomainException(string code, string message)
    : DomainException(code, message);
