using BuildingBlocks.Domain;

namespace Modules.Loans.Domain;

public sealed class LoansDomainException(string code, string message)
    : DomainException(code, message);
