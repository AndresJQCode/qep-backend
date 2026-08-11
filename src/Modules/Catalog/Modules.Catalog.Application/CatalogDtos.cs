namespace Modules.Catalog.Application;

public sealed record ProductDto(
    Guid Id,
    string Name,
    string Code,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProductResponse(
    Guid Id,
    string Name,
    string Code,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProductsResponse(IReadOnlyCollection<ProductResponse> Items);

// IsActive does not travel in the requests: a product is born active and only changes
// through /deactivate. An editable boolean would turn deactivation into an ordinary PUT and
// leave it without its own audit entry, the same reasoning that kept suspend apart from
// editing roles in AUTH-06.
public sealed record CreateProductRequest(string Name, string Code);

public sealed record UpdateProductRequest(string Name, string Code);
