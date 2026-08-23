namespace Modules.Pricing.Application;

// IsActive no viaja en los requests: una lista nace activa y sólo cambia por /deactivate y
// /activate, mismo criterio que ClientClassification y TaxRate.
public sealed record PriceListDto(
    Guid Id,
    string Name,
    string Prefix,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PriceListResponse(
    Guid Id,
    string Name,
    string Prefix,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PriceListsResponse(IReadOnlyCollection<PriceListResponse> Items);

public sealed record CreatePriceListRequest(string Name, string Prefix);

public sealed record UpdatePriceListRequest(string Name, string Prefix);
