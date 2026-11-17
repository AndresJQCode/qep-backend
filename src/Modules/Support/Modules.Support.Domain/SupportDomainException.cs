using BuildingBlocks.Domain;

namespace Modules.Support.Domain;

public sealed class SupportDomainException(string code, string message)
    : DomainException(code, message);
