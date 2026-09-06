namespace Modules.Quotations.Application;

public sealed record QuotationItemDto(
    Guid Id,
    Guid ProductId,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    decimal DiscountAmount,
    decimal Subtotal,
    int TaxPercentage,
    decimal TaxAmount,
    int Position);

public sealed record QuotationDto(
    Guid Id,
    string QuotationNumber,
    Guid ClientId,
    Guid AdvisorId,
    // Status es texto y no el enum del dominio: ningún DTO expone un enum de dominio
    // directamente, mismo criterio que PriceScaleResponse.Restriction en Catalog.
    string Status,
    DateTimeOffset CreatedAt,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    decimal Subtotal,
    decimal TaxPercentage,
    decimal TaxAmount,
    decimal DiscountAmount,
    decimal Total,
    // CustomerVatSurplus viaja para que el frontend pueda mostrar "exento por excedente de
    // IVA" en vez de adivinar por que TaxAmount dio cero. RetentionAmount/NetTotal son el
    // snapshot de retencion en la fuente (Quotation.RecalculateTotals): NetTotal = Total -
    // RetentionAmount es lo que efectivamente se cobra en efectivo.
    bool CustomerVatSurplus,
    decimal RetentionAmount,
    decimal NetTotal,
    string? Notes,
    /// <summary>Sólo las partes que difieren del cliente. Una cotización que factura y entrega
    /// a los datos del cliente llega con la lista vacía.</summary>
    IReadOnlyCollection<QuotationPartyDto> Parties,
    Guid CreatedBy,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SentAt,
    Guid? PdfFileId,
    IReadOnlyCollection<QuotationItemDto> Items);

/// <summary>Una parte (facturación o entrega) tal como sale hacia el cliente HTTP. Role es texto
/// y no el enum del dominio, mismo criterio que Status.</summary>
public sealed record QuotationPartyDto(
    Guid Id,
    string Role,
    string? Name,
    string? Phone,
    string? Email,
    string? Address,
    Guid? DepartmentId,
    Guid? CityId);

/// <summary>Los datos de una parte tal como viajan en el request (US-6). Cada campo null es
/// "para éste, el del cliente".</summary>
public sealed record QuotationPartyRequest(
    string? Name,
    string? Phone,
    string? Email,
    string? Address,
    Guid? DepartmentId,
    Guid? CityId);

/// <summary>Las dos partes de la cotización en el request. <b>Null es el caso normal</b>: "factura
/// (o entrega) a los datos del cliente" — el switch prendido de la UI. Como
/// <c>UpdateQuotationRequest</c> reemplaza el recurso entero, mandar null en una parte que tenía
/// datos propios los borra y vuelve a los del cliente.</summary>
public sealed record QuotationPartiesRequest(
    QuotationPartyRequest? Billing,
    QuotationPartyRequest? Shipping);

public sealed record CreateQuotationRequest(
    Guid ClientId,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties);

public sealed record UpdateQuotationRequest(
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties);

public sealed record AddQuotationItemRequest(Guid ProductId, decimal Quantity);

public sealed record UpdateQuotationItemRequest(decimal Quantity);

/// <summary>US-12: el PDF ya se subió a Storage (flujo de carga firmada ya existente) antes de
/// esta llamada; acá sólo se referencia el archivo resultante.</summary>
public sealed record SendQuotationRequest(Guid PdfFileId);

/// <summary>El cliente tal como lo muestra la pantalla de la cotización, con su libreta de
/// direcciones. Viaja acá para que el detalle y el editor no pidan la ficha completa a
/// Customers en una segunda consulta.</summary>
public sealed record QuotationClientResponse(
    Guid Id,
    string Cuc,
    string Name,
    string? Phone,
    string? Email,
    string? Address,
    Guid? CityId,
    string? CityName,
    Guid? DepartmentId,
    string? DepartmentName,
    bool WithRetention,
    bool VatSurplus,
    bool IsActive,
    /// <summary>Última edición de la ficha. La pantalla la usa para decir desde cuándo un
    /// cliente está inactivo.</summary>
    DateTimeOffset UpdatedAt,
    IReadOnlyCollection<QuotationClientAddressResponse> Addresses);

public sealed record QuotationClientAddressResponse(
    Guid Id,
    string Name,
    string Address,
    string? Phone,
    Guid CityId,
    string CityName,
    Guid DepartmentId,
    string DepartmentName,
    bool IsPrincipal);

public sealed record QuotationItemPriceScaleResponse(
    int FromUnit,
    int ToUnit,
    decimal Discount);

public sealed record QuotationResponse(
    Guid Id,
    string QuotationNumber,
    Guid ClientId,
    /// <summary>Null sólo si el cliente ya no existe: `ClientId` es una referencia blanda entre
    /// módulos y una cotización histórica tiene que poder leerse igual.</summary>
    QuotationClientResponse? Client,
    Guid AdvisorId,
    string? AdvisorEmail,
    string Status,
    DateTimeOffset CreatedAt,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    decimal Subtotal,
    decimal TaxPercentage,
    decimal TaxAmount,
    decimal DiscountAmount,
    decimal Total,
    // CustomerVatSurplus viaja para que el frontend pueda mostrar "exento por excedente de
    // IVA" en vez de adivinar por que TaxAmount dio cero. RetentionAmount/NetTotal son el
    // snapshot de retencion en la fuente (Quotation.RecalculateTotals): NetTotal = Total -
    // RetentionAmount es lo que efectivamente se cobra en efectivo.
    bool CustomerVatSurplus,
    decimal RetentionAmount,
    decimal NetTotal,
    string? Notes,
    IReadOnlyCollection<QuotationPartyResponse> Parties,
    Guid CreatedBy,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SentAt,
    Guid? PdfFileId,
    IReadOnlyCollection<QuotationItemResponse> Items);

public sealed record QuotationPartyResponse(
    Guid Id,
    string Role,
    string? Name,
    string? Phone,
    string? Email,
    string? Address,
    Guid? DepartmentId,
    Guid? CityId);

public sealed record QuotationListItemResponse(
    Guid Id,
    string QuotationNumber,
    Guid ClientId,
    string? ClientName,
    Guid AdvisorId,
    string? AdvisorEmail,
    string Status,
    DateTimeOffset CreatedAt,
    decimal Total);

public sealed record QuotationsPageResponse(
    IReadOnlyCollection<QuotationListItemResponse> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record QuotationItemResponse(
    Guid Id,
    Guid ProductId,
    /// <summary>Nombre, código, portada y escalas del producto, resueltos por el backend. Sin
    /// esto la pantalla tenía que traerse el catálogo entero para poner un nombre en cada
    /// línea. Vacíos si el producto ya no existe.</summary>
    string ProductName,
    string ProductCode,
    string? ProductImageUrl,
    IReadOnlyCollection<QuotationItemPriceScaleResponse> PriceScales,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    decimal DiscountAmount,
    decimal Subtotal,
    int TaxPercentage,
    decimal TaxAmount,
    int Position);
