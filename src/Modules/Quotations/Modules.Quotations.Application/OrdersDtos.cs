namespace Modules.Quotations.Application;

public sealed record OrderPaymentProofDto(
    Guid Id, Guid FileId, decimal Amount, DateTimeOffset UploadedAt, DateOnly? PaidOn);

public sealed record OrderHistoryEntryDto(
    Guid Id, string EventType, DateTimeOffset EventAt, Guid? MemberId, string? Details);

/// <summary>
/// El pedido con sus datos propios (spec 2026-10-15 §4). Ningún campo es un enum del dominio:
/// <c>Source</c>, <c>Status</c> y <c>PaymentStatus</c> viajan con el nombre del enum, mismo criterio
/// que <c>QuotationDto.Status</c>. <c>CustomerVatSurplus</c> es el valor **efectivo**
/// (<c>Order.AppliesVatSurplus</c>): facturando a consumidor final el IVA se cobra aunque el cliente
/// tenga excedente, y la pantalla imprime «exento» con sólo ver esto en true. <c>BillingAccount</c> es null
/// sólo en el cálculo previo sin cuenta (la pantalla precia la línea antes de elegir la empresa que
/// factura); un pedido guardado siempre la trae.
/// </summary>
public sealed record OrderDto(
    Guid Id,
    string OrderNumber,
    string Source,
    Guid? QuotationId,
    string? ExternalReference,
    Guid ClientId,
    Guid AdvisorId,
    string Status,
    string PaymentStatus,
    string? PaymentMethod,
    string Currency,
    bool IsRetail,
    int? GlobalScaleFloor,
    bool BillingUsesBusinessName,
    bool IsStorePickup,
    bool BillsToFinalConsumer,
    bool? BillingWithRetention,
    bool? BillingVatSurplus,
    bool CustomerVatSurplus,
    decimal Subtotal,
    decimal TaxPercentage,
    decimal TaxAmount,
    decimal DiscountAmount,
    decimal Total,
    decimal RetentionAmount,
    decimal NetTotal,
    string? Notes,
    QuotationBillingAccountDto? BillingAccount,
    IReadOnlyCollection<QuotationPartyDto> Parties,
    IReadOnlyCollection<QuotationItemDto> Items,
    DateTimeOffset ConvertedAt,
    Guid ConvertedBy,
    DateTimeOffset? ApprovedAt,
    Guid? ApprovedBy,
    DateTimeOffset? CancelledAt,
    Guid? CancelledBy,
    string? CancellationReason,
    DateTimeOffset? InvoicedAt,
    Guid? InvoicedBy,
    string? RitualCollectionSyncId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    IReadOnlyCollection<OrderPaymentProofDto> PaymentProofs);

/// <summary>El detalle: el pedido, el número de la cotización de origen (si la hay) y su historial.
/// Ya no viaja la cotización entera (spec 2026-10-15 §6.2).</summary>
public sealed record OrderDetailDto(
    OrderDto Order,
    string? QuotationNumber,
    IReadOnlyCollection<OrderHistoryEntryDto> History);

public sealed record OrderPaymentProofRequest(Guid FileId, decimal Amount, DateOnly? PaidOn = null);

public sealed record OrderPaymentProofUpdateRequest(
    Guid ProofId, decimal Amount, Guid? NewFileId = null, DateOnly? PaidOn = null);

/// <summary>
/// El cuerpo de un pedido manual (spec 2026-10-15 §5.2) y de su cálculo previo: el mismo que la
/// pantalla arma para una cotización, más el estado de pago y los comprobantes, porque un pedido nace
/// vendido. <c>Items</c> lleva producto y cantidad: el precio, el descuento por escala y la tasa los
/// resuelve el backend, igual que al convertir. <c>BillingAccount</c> es obligatoria al crear y opcional en
/// el cálculo previo, que sin ella precia en la moneda por defecto del tenant.
/// </summary>
public sealed record CreateOrderRequest(
    Guid ClientId,
    string? PaymentMethod,
    string? Notes,
    bool IsRetail,
    int? GlobalScaleFloor,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    IReadOnlyList<OrderEditItemRequest> Items,
    string PaymentStatus,
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs);

public sealed record ConvertQuotationToOrderRequest(
    string PaymentStatus,
    string? Notes,
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs);

public sealed record AddOrderPaymentProofsRequest(
    string PaymentStatus,
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs,
    string? Notes = null,
    IReadOnlyCollection<OrderPaymentProofUpdateRequest>? UpdatedProofs = null);

public sealed record OrderItemAdditionRequest(Guid ProductId, decimal Quantity);

public sealed record AddOrderItemsRequest(IReadOnlyList<OrderItemAdditionRequest> ToAdd);

public sealed record CancelOrderRequest(string? Reason);

public sealed record OrderEditItemRequest(Guid ProductId, decimal Quantity);

public sealed record OrderEditProofAddRequest(Guid? FileId, decimal Amount, DateOnly? PaidOn = null);

public sealed record OrderEditProofsRequest(
    IReadOnlyList<OrderEditProofAddRequest>? Add,
    IReadOnlyList<OrderPaymentProofUpdateRequest>? Update,
    IReadOnlyList<Guid>? RemoveIds);

/// <summary>El guardado atómico de «Editar» pedido (spec 2026-09-17). <c>IsRetail</c> y
/// <c>GlobalScaleFloor</c> son del pedido desde el spec 2026-10-15.</summary>
public sealed record SaveOrderEditsRequest(
    IReadOnlyList<OrderEditItemRequest>? Items,
    OrderEditProofsRequest? Proofs,
    string? Notes,
    int? GlobalScaleFloor = null,
    bool IsRetail = false);

public sealed record OrderPaymentProofResponse(
    Guid Id, Guid FileId, decimal Amount, DateTimeOffset UploadedAt, DateOnly? PaidOn);

/// <summary>Espeja <see cref="OrderDto"/>, incluido el <c>BillingAccount</c> null del cálculo previo sin
/// cuenta. Las líneas no viajan acá: van resueltas (nombre, código, imagen, escalas) en
/// <see cref="OrderDetailResponse.Items"/>.</summary>
public sealed record OrderResponse(
    Guid Id,
    string OrderNumber,
    string Source,
    Guid? QuotationId,
    string? ExternalReference,
    Guid ClientId,
    Guid AdvisorId,
    string Status,
    string PaymentStatus,
    string? PaymentMethod,
    string Currency,
    bool IsRetail,
    int? GlobalScaleFloor,
    bool BillingUsesBusinessName,
    bool IsStorePickup,
    bool BillsToFinalConsumer,
    bool? BillingWithRetention,
    bool? BillingVatSurplus,
    bool CustomerVatSurplus,
    decimal Subtotal,
    decimal TaxPercentage,
    decimal TaxAmount,
    decimal DiscountAmount,
    decimal Total,
    decimal RetentionAmount,
    decimal NetTotal,
    string? Notes,
    QuotationBillingAccountDto? BillingAccount,
    IReadOnlyCollection<QuotationPartyResponse> Parties,
    DateTimeOffset ConvertedAt,
    Guid ConvertedBy,
    DateTimeOffset? ApprovedAt,
    Guid? ApprovedBy,
    DateTimeOffset? CancelledAt,
    Guid? CancelledBy,
    string? CancellationReason,
    DateTimeOffset? InvoicedAt,
    Guid? InvoicedBy,
    string? RitualCollectionSyncId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    IReadOnlyCollection<OrderPaymentProofResponse> PaymentProofs);

/// <summary>Fila de la lista de pedidos. <c>QuotationId</c> y <c>QuotationNumber</c> son opcionales
/// desde el spec 2026-10-15: un pedido manual o externo no tiene cotización. <c>Source</c> y
/// <c>ExternalReference</c> van al final para que la tabla dibuje la insignia de origen y el
/// <c>#1001</c> debajo del número sin pedir el detalle.</summary>
public sealed record OrderListItemResponse(
    Guid Id,
    string OrderNumber,
    Guid? QuotationId,
    string? QuotationNumber,
    Guid ClientId,
    string? ClientName,
    Guid AdvisorId,
    string? AdvisorName,
    string Status,
    string PaymentStatus,
    string? PaymentMethod,
    DateTimeOffset ConvertedAt,
    string Currency,
    decimal Total,
    string Source,
    string? ExternalReference);

public sealed record SourceQuotationResponse(Guid Id, string QuotationNumber);

public sealed record OrderHistoryEntryResponse(
    Guid Id, string EventType, DateTimeOffset EventAt, Guid? MemberId, string? MemberName, string? Details);

/// <summary>
/// El detalle de un pedido para la pantalla (spec 2026-10-15 §6.2): el pedido, el cliente y el asesor
/// resueltos, la cuenta de cobro con su empresa, las líneas con su producto, la cotización de origen
/// cuando la hay, el historial y los pisos de escala disponibles. Deja de viajar la cotización.
/// </summary>
public sealed record OrderDetailResponse(
    OrderResponse Order,
    QuotationClientResponse? Client,
    string? AdvisorName,
    string? AdvisorEmail,
    QuotationBillingResponse? BillingAccount,
    IReadOnlyCollection<QuotationItemResponse> Items,
    SourceQuotationResponse? SourceQuotation,
    IReadOnlyCollection<OrderHistoryEntryResponse> History,
    int[] AvailableFloors);

public sealed record OrdersPageResponse(
    IReadOnlyCollection<OrderListItemResponse> Items,
    int Total,
    int Page,
    int PageSize);
