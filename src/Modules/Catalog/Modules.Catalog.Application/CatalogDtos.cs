namespace Modules.Catalog.Application;

public sealed record ProductDto(
    Guid Id,
    string Name,
    string Code,
    bool IsActive,
    string? Description,
    Guid? ImageFileId,
    string? ImageUrl,
    Guid? TaxRateId,
    decimal? PriceBaseUsd,
    decimal? PriceBaseCop,
    IReadOnlyCollection<PriceScaleResponse> PriceScales,
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
    Guid? TaxRateId,
    // CAT-09. El precio en dos monedas fijas — reemplazó por completo al viejo Price, retirado.
    decimal? PriceBaseUsd,
    decimal? PriceBaseCop,
    IReadOnlyCollection<PriceScaleResponse> PriceScales,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Precio y escalas de un producto (CAT-09), tal como los manda el cliente. Al menos uno de
/// <c>BaseUsd</c>/<c>BaseCop</c> es obligatorio — el dominio lo exige incondicionalmente, así
/// que ningún producto se crea sin esto.
/// </summary>
public sealed record ProductPricingRequest(
    decimal? BaseUsd,
    decimal? BaseCop,
    IReadOnlyCollection<PriceScaleRequest>? Scales);

/// <summary>
/// Restriction es texto ("multiple" | "packaging_unit") y no el enum del dominio: ningún DTO
/// expone <c>PriceScaleRestriction</c> directamente, mismo criterio que
/// <c>MembershipListItemResponse.State</c>.
/// </summary>
public sealed record PriceScaleRequest(
    int FromUnit,
    int ToUnit,
    decimal Discount,
    string? Restriction,
    int? Multiple,
    int? PackagingUnit,
    decimal? FinalUsd,
    decimal? FinalCop);

public sealed record PriceScaleResponse(
    Guid Id,
    int FromUnit,
    int ToUnit,
    decimal Discount,
    string Restriction,
    int? Multiple,
    int? PackagingUnit,
    decimal? FinalUsd,
    decimal? FinalCop);

/// <summary>El sobre del listado, con el total que la paginación necesita — mismo criterio que
/// `CustomersResponse`.</summary>
/// <summary>
/// La respuesta del 202 de exportacion. No lleva el enlace a proposito: el archivo llega por
/// correo, y devolverlo tambien aca duplicaria el canal de entrega.
/// </summary>
public sealed record ProductExportResponse(
    string FileName,
    int ProductCount,
    DateTimeOffset ExpiresAt);

public sealed record ProductsResponse(
    IReadOnlyCollection<ProductResponse> Items,
    int Total,
    int Page,
    int PageSize);

// IsActive no viaja en los requests: un producto nace activo y sólo cambia por
// /deactivate. Un booleano editable convertiría la desactivación en un PUT común y la
// dejaría sin su propia entrada de auditoría, el mismo razonamiento que mantuvo suspender
// aparte de editar roles en AUTH-06.
//
// Description/ImageFileId/TaxRateId sí viajan, y son opcionales: un producto sin ninguno
// sigue siendo válido. En el PUT, mandarlos en null los **limpia** — el verbo reemplaza el
// recurso entero. Pricing es la excepción: no es opcional, porque el precio en al menos una
// moneda es obligatorio incondicionalmente (CAT-09).
public sealed record CreateProductRequest(
    string Name,
    string Code,
    string? Description,
    Guid? ImageFileId,
    Guid? TaxRateId,
    ProductPricingRequest Pricing);

public sealed record UpdateProductRequest(
    string Name,
    string Code,
    string? Description,
    Guid? ImageFileId,
    Guid? TaxRateId,
    ProductPricingRequest Pricing);

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
