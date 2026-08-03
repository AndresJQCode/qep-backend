using BuildingBlocks.Domain;

namespace Modules.Identity.Domain;

public sealed class IdentityDomainException(string code, string message)
    : DomainException(code, message);
