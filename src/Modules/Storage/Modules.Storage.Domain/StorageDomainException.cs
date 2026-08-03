using BuildingBlocks.Domain;

namespace Modules.Storage.Domain;

public sealed class StorageDomainException(string code, string message)
    : DomainException(code, message);
