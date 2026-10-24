using BuildingBlocks.Domain;

namespace Modules.Suppliers.Domain;

public sealed class SuppliersDomainException(string code, string message)
    : DomainException(code, message);
