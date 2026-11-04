using BuildingBlocks.Domain;

namespace Modules.Store.Domain;

public sealed class StoreDomainException(string code, string message)
    : DomainException(code, message);
