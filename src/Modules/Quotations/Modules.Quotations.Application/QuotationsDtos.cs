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
    string? BillingNameOverride,
    string? BillingAddressOverride,
    string? DeliveryAddressOverride,
    string? DeliveryCityOverride,
    Guid CreatedBy,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SentAt,
    Guid? PdfFileId,
    IReadOnlyCollection<QuotationItemDto> Items);

/// <summary>Sobrescrituras de facturación/entrega tal como viajan en el request (US-6). Null =
/// no se toca / usa el dato del cliente maestro.</summary>
public sealed record QuotationOverridesRequest(
    string? BillingName,
    string? BillingAddress,
    string? DeliveryAddress,
    string? DeliveryCity);

public sealed record CreateQuotationRequest(
    Guid ClientId,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationOverridesRequest? Overrides);

public sealed record UpdateQuotationRequest(
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationOverridesRequest? Overrides);

public sealed record AddQuotationItemRequest(Guid ProductId, decimal Quantity);

public sealed record UpdateQuotationItemRequest(decimal Quantity);

/// <summary>US-12: el PDF ya se subió a Storage (flujo de carga firmada ya existente) antes de
/// esta llamada; acá sólo se referencia el archivo resultante.</summary>
public sealed record SendQuotationRequest(Guid PdfFileId);

public sealed record QuotationResponse(
    Guid Id,
    string QuotationNumber,
    Guid ClientId,
    Guid AdvisorId,
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
    string? BillingNameOverride,
    string? BillingAddressOverride,
    string? DeliveryAddressOverride,
    string? DeliveryCityOverride,
    Guid CreatedBy,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SentAt,
    Guid? PdfFileId,
    IReadOnlyCollection<QuotationItemResponse> Items);

public sealed record QuotationListItemResponse(
    Guid Id,
    string QuotationNumber,
    Guid ClientId,
    Guid AdvisorId,
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
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    decimal DiscountAmount,
    decimal Subtotal,
    int TaxPercentage,
    decimal TaxAmount,
    int Position);
