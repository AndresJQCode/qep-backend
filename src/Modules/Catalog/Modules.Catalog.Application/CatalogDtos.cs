namespace Modules.Catalog.Application;

public sealed record ProductDto(
    Guid Id,
    string Name,
    string Code,
    bool IsActive,
    string? Description,
    Guid? ImageFileId,
    string? ImageUrl,
    decimal? Price,
    string? Currency,
    Guid? TaxRateId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProductResponse(
    Guid Id,
    string Name,
    string Code,
    bool IsActive,
    string? Description,
    Guid? ImageFileId,
    // CAT-05b. Derivado y de sólo lectura: dónde se sirve el archivo es de Storage, no de
    // catalog. Viene en null si la imagen no fue publicada. `ImageFileId` se mantiene porque es
    // lo que el cliente manda de vuelta en el PUT.
    string? ImageUrl,
    decimal? Price,
    string? Currency,
    Guid? TaxRateId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProductsResponse(IReadOnlyCollection<ProductResponse> Items);

// IsActive no viaja en los requests: un producto nace activo y sólo cambia por
// /deactivate. Un booleano editable convertiría la desactivación en un PUT común y la
// dejaría sin su propia entrada de auditoría, el mismo razonamiento que mantuvo suspender
// aparte de editar roles en AUTH-06.
//
// Los cinco de CAT-04 sí viajan, y son opcionales: un producto sin ninguno sigue siendo válido.
// En el PUT, mandarlos en null los **limpia** — el verbo reemplaza el recurso entero.
public sealed record CreateProductRequest(
    string Name,
    string Code,
    string? Description,
    Guid? ImageFileId,
    decimal? Price,
    string? Currency,
    Guid? TaxRateId);

public sealed record UpdateProductRequest(
    string Name,
    string Code,
    string? Description,
    Guid? ImageFileId,
    decimal? Price,
    string? Currency,
    Guid? TaxRateId);

public sealed record TaxRateDto(
    Guid Id,
    string Name,
    int Percentage,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TaxRateResponse(
    Guid Id,
    string Name,
    int Percentage,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TaxRatesResponse(IReadOnlyCollection<TaxRateResponse> Items);

// Percentage es int, no decimal: P-008, decidido por el owner el 2026-08-10, fija el porcentaje
// en 0 decimales. Encaja con el IVA colombiano —19, 5 o 0— y el gate CAT-00 lo declara como
// límite de alcance del módulo, o sea que no admite retenciones con fracción.
//
// IsActive tampoco viaja acá, por la misma razón que en producto.
public sealed record CreateTaxRateRequest(string Name, int Percentage);

public sealed record UpdateTaxRateRequest(string Name, int Percentage);
