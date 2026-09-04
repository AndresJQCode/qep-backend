using BuildingBlocks.Domain;

namespace Modules.Reporting.Domain;

public sealed class ReportingDomainException(string code, string message)
    : DomainException(code, message);
