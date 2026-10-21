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
    IReadOnlyCollection<OrderPaymentProofDto> PaymentProofs,
    /// <summary>La entrega por envío, con forma fija también en recogida. Ver <see cref="ShippingDto"/>.</summary>
    ShippingDto Shipping,
    /// <summary>Envío sin IVA que suma a <c>Total</c>; 0 salvo <c>Charged</c>. <c>Subtotal</c> y
    /// <c>TaxAmount</c> siguen siendo sólo productos (spec 2026-10-17 §5.3).</summary>
    decimal FreightSubtotal,
    /// <summary>IVA del envío que suma a <c>Total</c>; 0 con excedente de IVA o fuera de COP.</summary>
    decimal FreightTaxAmount,
    /// <summary>Cómo llamó la tienda al envío (spec de Shopify §6.2.5); null fuera del camino externo. En la
    /// respuesta viaja dentro de <c>shipping</c> (<see cref="OrderShippingResponse"/>).</summary>
    string? ExternalShippingTitle);

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
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs,
    /// <summary>La entrega por envío; null es "sin envío". Ver <see cref="ShippingRequest"/>. Con
    /// <c>CashOnDelivery</c>, el <c>PaymentStatus</c> tiene que ser <c>PaymentPending</c> (spec 2026-10-17 §6).</summary>
    ShippingRequest? Shipping = null);

public sealed record ConvertQuotationToOrderRequest(
    string PaymentStatus,
    string? Notes,
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs);

public sealed record AddOrderPaymentProofsRequest(
    string PaymentStatus,
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs,
    string? Notes = null,
    IReadOnlyCollection<OrderPaymentProofUpdateRequest>? UpdatedProofs = null);

/// <summary>Una línea a agregar al pedido, por variante (spec 2026-10-20 §4; antes <c>productId</c>).</summary>
public sealed record OrderItemAdditionRequest(Guid VariantId, decimal Quantity);

public sealed record AddOrderItemsRequest(IReadOnlyList<OrderItemAdditionRequest> ToAdd);

public sealed record CancelOrderRequest(string? Reason);

/// <summary>«Reconocer este nombre» (spec de Shopify D-S25). Sólo la transportadora y si se recuerda el título:
/// el título lo pone el pedido (<c>shipping.externalShippingTitle</c>), no la pantalla, así que no se puede
/// recordar uno distinto del que llegó de la tienda.</summary>
public sealed record RecognizeOrderCarrierRequest(Guid CarrierId, bool RememberName);

/// <summary>Una línea del estado deseado del pedido, por variante (spec 2026-10-20 §4; antes
/// <c>productId</c>): dos tallas del mismo producto son dos líneas.</summary>
public sealed record OrderEditItemRequest(Guid VariantId, decimal Quantity);

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
    bool IsRetail = false,
    /// <summary>El envío del pedido pendiente (spec 2026-10-17 §5.4). <b>Ausente deja el guardado</b>: un
    /// frontend viejo no lo borra. No cambia recoger o enviar, que el pedido no edita.</summary>
    ShippingRequest? Shipping = null);

/// <summary>
/// El envío del detalle del pedido: los campos de <see cref="ShippingResponse"/> más
/// <see cref="ExternalShippingTitle"/>, el nombre con el que la tienda llamó al envío cuando QEP no reconoció la
/// transportadora (spec de Shopify §6.2.5). BFF: viaja dentro de <c>shipping</c> para que la pantalla ofrezca
/// «Reconocer este nombre» al lado de «Elegir transportadora» sin leer el espejo de Shopify. Es un tipo aparte y
/// no un campo nuevo de <see cref="ShippingResponse"/> porque las rutas de cotizaciones no cambian, y la fila de
/// la lista no lo necesita.
/// </summary>
public sealed record OrderShippingResponse(
    bool IsStorePickup,
    ShippingCarrierResponse? Carrier,
    string? ChargeMode,
    decimal FreightAmount,
    int FreightTaxPercentage,
    decimal FreightSubtotal,
    decimal FreightTaxAmount,
    bool CashOnDelivery,
    string? ExternalShippingTitle)
{
    public static OrderShippingResponse From(ShippingDto shipping, string? externalShippingTitle)
    {
        ArgumentNullException.ThrowIfNull(shipping);
        var common = ShippingResponse.From(shipping);
        return new(
            common.IsStorePickup, common.Carrier, common.ChargeMode, common.FreightAmount, common.FreightTaxPercentage,
            common.FreightSubtotal, common.FreightTaxAmount, common.CashOnDelivery, externalShippingTitle);
    }
}

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
    IReadOnlyCollection<OrderPaymentProofResponse> PaymentProofs,
    /// <summary>La entrega por envío. El motivo BFF de su forma está en <see cref="ShippingDto"/>; la del pedido
    /// suma <c>externalShippingTitle</c> (<see cref="OrderShippingResponse"/>).</summary>
    OrderShippingResponse Shipping,
    /// <summary>Envío sin IVA que suma a <c>Total</c>. Un total que no cierra se explica con un campo:
    /// <c>Subtotal + TaxAmount + FreightSubtotal + FreightTaxAmount = Total</c> (spec 2026-10-17 §5.3).</summary>
    decimal FreightSubtotal,
    /// <summary>IVA del envío que suma a <c>Total</c>.</summary>
    decimal FreightTaxAmount);

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
    string? ExternalReference,
    /// <summary>La entrega por envío de la fila, misma forma que en el detalle (con el subtotal y el IVA del
    /// envío adentro): la grilla muestra transportadora y contraentrega sin pedir el pedido entero.</summary>
    ShippingResponse Shipping);

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
