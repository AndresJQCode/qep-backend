using BuildingBlocks.Domain;

namespace Modules.Customers.Domain;

public sealed class CustomersDomainException(string code, string message)
    : DomainException(code, message);
