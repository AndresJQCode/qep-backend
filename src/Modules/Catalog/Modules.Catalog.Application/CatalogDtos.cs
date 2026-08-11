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

// IsActive no viaja en los requests: un producto nace activo y sólo cambia por
// /deactivate. Un booleano editable convertiría la desactivación en un PUT común y la
// dejaría sin su propia entrada de auditoría, el mismo razonamiento que mantuvo suspender
// aparte de editar roles en AUTH-06.
public sealed record CreateProductRequest(string Name, string Code);

public sealed record UpdateProductRequest(string Name, string Code);
