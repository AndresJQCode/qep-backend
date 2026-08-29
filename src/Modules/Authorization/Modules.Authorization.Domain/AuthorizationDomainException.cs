using BuildingBlocks.Domain;

namespace Modules.Authorization.Domain;

public sealed class AuthorizationDomainException(string code, string message)
    : DomainException(code, message);
