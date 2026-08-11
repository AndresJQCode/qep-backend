using BuildingBlocks.Domain;

namespace Modules.Catalog.Domain;

public sealed class CatalogDomainException(string code, string message)
    : DomainException(code, message);
