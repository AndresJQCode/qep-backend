using BuildingBlocks.Domain;

namespace Modules.Companies.Domain;

public sealed class CompaniesDomainException(string code, string message)
    : DomainException(code, message);
