using BuildingBlocks.Domain;

namespace Modules.ApiAccess.Domain;

public sealed class ApiAccessDomainException(string code, string message)
    : DomainException(code, message);
