using BuildingBlocks.Domain;

namespace Modules.Geography.Domain;

public sealed class GeographyDomainException(string code, string message)
    : DomainException(code, message);
