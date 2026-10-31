using BuildingBlocks.Domain;

namespace Modules.ServiceContracts.Domain;

public sealed class ServiceContractsDomainException(string code, string message)
    : DomainException(code, message);
