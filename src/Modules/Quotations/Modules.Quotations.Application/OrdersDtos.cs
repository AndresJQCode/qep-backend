using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modules.Quotations.Application;

public sealed record OrderPaymentProofDto(
    Guid Id, Guid FileId, decimal Amount, DateTimeOffset UploadedAt, DateOnly? PaidOn);

public sealed record OrderHistoryEntryDto(
    Guid Id, string EventType, DateTimeOffset EventAt, Guid? MemberId, string? Details);

/// <summary>
/// El pedido con sus datos propios (spec 2026-10-15 §4). Ningún campo es un enum del dominio:
/// <c>Source</c>, <c>Status</c> (<c>Open</c>, <c>Archived</c>, <c>Cancelled</c>), <c>PaymentStatus</c> y
/// <c>FulfillmentStatus</c> viajan con el nombre del enum, mismo criterio que <c>QuotationDto.Status</c>. Los cuatro
/// ejes del spec 2026-11-10 viajan separados, con quién y cuándo de cada marca, para que la pantalla no los deduzca
/// del historial: <c>Invoiced</c> sigue en true en un pedido archivado. <c>CustomerVatSurplus</c> es el valor **efectivo**
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
    string FulfillmentStatus,
    bool Invoiced,
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
    DateTimeOffset? PaidMarkedAt,
    Guid? PaidMarkedBy,
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
    string? ExternalShippingTitle,
    /// <summary>Sólo las retenciones que aplicaron (spec impuestos por tipo §9.3); su suma es <c>RetentionAmount</c>.</summary>
    IReadOnlyList<WithholdingDto> Withholdings,
    /// <summary>Siempre vacía: el pedido tiene una sola fuente de tasas y viaja en
    /// <see cref="BillingWithholdingTaxRates"/>. Está para que la forma sea la de la cotización.</summary>
    IReadOnlyList<WithholdingTaxRateDto> CustomerWithholdingTaxRates,
    /// <summary>Las tasas vigentes del pedido, apliquen o no: lo que prellena los selectores de la edición (D21).</summary>
    IReadOnlyList<WithholdingTaxRateDto> BillingWithholdingTaxRates,
    DateTimeOffset? PreparedAt,
    Guid? PreparedBy,
    DateTimeOffset? ArchivedAt,
    Guid? ArchivedBy,
    /// <summary>Lo que falta por pagar: neto − consignado, con el redondeo de la moneda (pesos enteros en COP). Negativo
    /// es sobrante. Un total que no cierra se explica con un campo: la pantalla no resta comprobantes, que con la
    /// fracción de la retención le daría un saldo de centavos que no se ve (spec 2026-11-10 §3.2).</summary>
    decimal Balance,
    /// <summary>El municipio de la operación (spec ReteICA por empresa §7.5). No viaja en <c>OrderResponse</c>: el
    /// compositor del detalle lo resuelve a nombre en la raíz de <see cref="OrderDetailResponse"/> (plan P2).</summary>
    Guid? OperationCityId = null,
    /// <summary>La nota del comprador (tienda pública o Shopify), aparte de <see cref="Notes"/>, que es del asesor
    /// (spec 2026-11-14).</summary>
    string? BuyerNotes = null,
    /// <summary>La cuenta de comprador de un pedido de la tienda (spec 2026-11-15 §4). No viaja en <c>OrderResponse</c>: el
    /// compositor del detalle la resuelve a correo en la raíz de <see cref="OrderDetailResponse"/>.</summary>
    Guid? BuyerAccountId = null,
    /// <summary>El veredicto de cada tasa del pedido, aplique o no, en su orden (spec retenciones por UVT §6). Ver
    /// <see cref="QuotationDto.WithholdingBreakdown"/>.</summary>
    IReadOnlyList<WithholdingVerdictDto>? WithholdingBreakdown = null,
    /// <summary>La fecha del pedido en el calendario del tenant (spec 2026-11-16 §4), aparte de <see cref="ConvertedAt"/>,
    /// que no cambia. Lleva default sólo porque va después de los opcionales: <c>OrderMapping.ToDto</c> la escribe siempre.</summary>
    DateOnly OrderDate = default,
    /// <summary>Ver <see cref="Order.RequiresShipping"/> (spec 2026-11-18 §5). Default por la misma razón que el anterior.</summary>
    bool RequiresShipping = true,
    /// <summary>Mayorista fase 3: el vínculo con la orden del distribuidor, sin nombres resueltos. No viaja en
    /// <c>OrderResponse</c>: el compositor del detalle lo resuelve en la raíz (<see cref="OrderDetailResponse.Wholesale"/>).</summary>
    OrderWholesaleState? Wholesale = null,
    /// <summary>Quién emitió la factura: <c>siigo</c> si la creó la integración, <c>null</c> en una marca manual (spec 2026-11-18
    /// §5.5). BFF: la pantalla escribe «Facturado en Siigo FV-1-123» y esconde «Revertir facturación» sin conocer la integración;
    /// con un proveedor, esa acción responde 422: la marca se quita cuando la factura se anula allá y la integración lo verifica (spec
    /// 2026-11-25). El id de la factura en el proveedor no viaja.</summary>
    string? InvoiceProvider = null,
    /// <summary>El número de la factura del proveedor (<c>FV-1-123</c>); <c>null</c> en una marca manual.</summary>
    string? InvoiceNumber = null,
    /// <summary>Ver <see cref="OrderResponse.DeliveryEditable"/>: <see cref="Order.IsEditable"/> del pedido.</summary>
    bool DeliveryEditable = false);

/// <summary>Los dos valores de la aprobación mayorista (D-1), por nombre en el JSON.</summary>
public static class WholesaleApprovals
{
    public const string Pending = "Pending";

    public const string Approved = "Approved";
}

/// <summary>El vínculo de un pedido mayorista tal como lo guarda el pedido; los nombres los resuelve el compositor.</summary>
public sealed record OrderWholesaleState(
    Guid BuyerTenantId,
    Guid PurchaseOrderId,
    string PurchaseOrderNumber,
    string Approval,
    DateTimeOffset? ApprovedAt,
    Guid? ApprovedBy,
    bool CancelledByBuyer);

/// <summary>Un tenant con su nombre visible (el distribuidor, en el detalle de un pedido mayorista).</summary>
public sealed record OrderTenantRefDto(Guid Id, string? Name);

/// <summary>Una membresía con su nombre visible.</summary>
public sealed record OrderMemberRefDto(Guid MemberId, string? Name);

/// <summary>
/// Mayorista fase 3 (D-7 y adenda): lo que el mayorista ve del distribuidor en el detalle. El nombre del tenant es el que ya
/// muestra Distribuidores (ningún dato nuevo cruza); <c>Approval</c> es <c>Pending</c> o <c>Approved</c>. Null fuera de un
/// pedido mayorista.
/// </summary>
public sealed record OrderWholesaleDto(
    OrderTenantRefDto BuyerTenant,
    Guid PurchaseOrderId,
    string PurchaseOrderNumber,
    string Approval,
    DateTimeOffset? ApprovedAt,
    OrderMemberRefDto? ApprovedBy,
    bool CancelledByBuyer);

/// <summary>La fila mayorista de la lista: el número de la orden y la aprobación, para la insignia «Por aprobar».</summary>
public sealed record OrderListWholesaleDto(string PurchaseOrderNumber, string Approval);

/// <summary>El detalle: el pedido, el número de la cotización de origen (si la hay) y su historial.
/// Ya no viaja la cotización entera (spec 2026-10-15 §6.2). <c>QuotationCreatedAt</c> es el <c>CreatedAt</c> de esa
/// cotización (spec 2026-11-16 F-8); null sin cotización o en las rutas que no la leen.</summary>
public sealed record OrderDetailDto(
    OrderDto Order,
    string? QuotationNumber,
    IReadOnlyCollection<OrderHistoryEntryDto> History,
    DateTimeOffset? QuotationCreatedAt = null);

public sealed record OrderPaymentProofRequest(Guid FileId, decimal Amount, DateOnly? PaidOn = null);

public sealed record OrderPaymentProofUpdateRequest(
    Guid ProofId, decimal Amount, Guid? NewFileId = null, DateOnly? PaidOn = null);

/// <summary>
/// El cuerpo de un pedido manual (spec 2026-10-15 §5.2) y de su cálculo previo: el mismo que la
/// pantalla arma para una cotización, más los comprobantes, porque un pedido nace vendido. El estado de
/// pago no viaja: lo calcula el backend con los comprobantes (spec 2026-11-10 §3.2), y un cliente viejo
/// que todavía lo mande no falla, porque System.Text.Json ignora la propiedad. <c>Items</c> lleva
/// producto y cantidad: el precio, el descuento por escala y la tasa los resuelve el backend, igual que al
/// convertir. <c>BillingAccount</c> es obligatoria al crear y opcional en el cálculo previo, que sin ella
/// precia en la moneda por defecto del tenant.
/// </summary>
// Not sealed: PreviewOrderRequest extends it with the preview-only billingCompanyId.
public record CreateOrderRequest(
    Guid ClientId,
    string? PaymentMethod,
    string? Notes,
    bool IsRetail,
    int? GlobalScaleFloor,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    IReadOnlyList<OrderEditItemRequest> Items,
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs,
    /// <summary>La entrega por envío; null es "sin envío". Ver <see cref="ShippingRequest"/>. Con
    /// <c>CashOnDelivery</c> no puede llevar comprobantes (spec 2026-10-17 §6).</summary>
    ShippingRequest? Shipping = null)
{
    /// <summary>Los regalos del pedido (spec 2026-11-23): lista aparte de <c>Items</c>, que sigue siendo sólo las
    /// líneas pagadas. Ausente o null es «sin regalos». Una propiedad y no un parámetro posicional, para que
    /// <see cref="PreviewOrderRequest"/> la herede sin tocar su constructor.</summary>
    public IReadOnlyList<OrderGiftLineRequest>? Gifts { get; init; }
}

/// <summary>Un regalo del pedido (spec 2026-11-23): variante y cantidad; el precio es el de lista y el descuento el fijo
/// del 100 %.</summary>
public sealed record OrderGiftLineRequest(Guid VariantId, decimal Quantity);

/// <summary>
/// The body of <c>POST /orders/preview</c>: the manual order body plus <see cref="BillingCompanyId"/> (spec 2026-11-16
/// withholdings by company). Its own type so <c>POST /orders</c> never learns the field: an order keeps requiring the
/// full account.
/// </summary>
public sealed record PreviewOrderRequest(
    Guid ClientId,
    string? PaymentMethod,
    string? Notes,
    bool IsRetail,
    int? GlobalScaleFloor,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    IReadOnlyList<OrderEditItemRequest> Items,
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs,
    ShippingRequest? Shipping = null,
    /// <summary>The billing company chosen before its bank account: the preview paints its withholdings and answers
    /// <c>billingAccount: null</c>. Ignored when <c>billingAccount</c> is present.</summary>
    Guid? BillingCompanyId = null)
    : CreateOrderRequest(
        ClientId, PaymentMethod, Notes, IsRetail, GlobalScaleFloor, Parties, BillingAccount, Items, PaymentProofs, Shipping);

/// <summary>Sin estado de pago: lo calcula el backend con los comprobantes (spec 2026-11-10 §3.2).</summary>
public sealed record ConvertQuotationToOrderRequest(
    string? Notes,
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs);

/// <summary>Sin estado de pago: lo calcula el backend con los comprobantes (spec 2026-11-10 §3.2).</summary>
public sealed record AddOrderPaymentProofsRequest(
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs,
    string? Notes = null,
    IReadOnlyCollection<OrderPaymentProofUpdateRequest>? UpdatedProofs = null);

/// <summary>Una línea a agregar al pedido, por variante (spec 2026-10-20 §4; antes <c>productId</c>).</summary>
public sealed record OrderItemAdditionRequest(Guid VariantId, decimal Quantity);

public sealed record AddOrderItemsRequest(IReadOnlyList<OrderItemAdditionRequest> ToAdd);

/// <summary>Cuerpo de <c>PUT …/orders/{id}/buyer-account</c> (spec 2026-11-15 §4): el correo de la cuenta verificada.</summary>
public sealed record OrderBuyerAccountRequest(string? Email);

public sealed record CancelOrderRequest(string? Reason);

/// <summary>
/// <c>POST /orders/{orderId}/send</c>. Mismo contrato que <see cref="SendQuotationRequest"/>:
/// <paramref name="Recipient"/> es <c>"Customer"</c> (default) o <c>"Billing"</c>, una opción cerrada
/// que el backend resuelve contra lo que el pedido ya guarda, nunca un teléfono suelto. <b>El cuerpo
/// entero es opcional</b>: un cuerpo requerido rompió el envío de la cotización con 500
/// (<c>SendWorksWithoutABody</c>). <paramref name="ConnectionId"/> (spec 2026-11-09 D8): la conexión
/// elegida cuando hay varias disponibles para pedidos, como en la cotización. <paramref name="ContactId"/> y
/// <paramref name="ThreadConnectionId"/> (spec 2026-11-10 §4.6): ver <see cref="SendQuotationRequest"/>.
/// </summary>
public sealed record SendOrderRequest(
    string? Recipient = null, Guid? ConnectionId = null, Guid? ContactId = null, Guid? ThreadConnectionId = null);

/// <summary>«Reconocer este nombre» (spec de Shopify D-S25). Sólo la transportadora y si se recuerda el título:
/// el título lo pone el pedido (<c>shipping.externalShippingTitle</c>), no la pantalla, así que no se puede
/// recordar uno distinto del que llegó de la tienda.</summary>
public sealed record RecognizeOrderCarrierRequest(Guid CarrierId, bool RememberName);

/// <summary>Una línea del estado deseado del pedido, por variante (spec 2026-10-20 §4; antes
/// <c>productId</c>): dos tallas del mismo producto son dos líneas.</summary>
/// <summary>Una línea de la edición. <c>UnitPrice</c> (IVA incluido, mayor o igual a 0) sólo en un pedido mayorista sin aprobar
/// (D-4); en otro origen es 422 <c>order.item.unit_price_not_editable</c>.</summary>
public sealed record OrderEditItemRequest(Guid VariantId, decimal Quantity, decimal? UnitPrice = null);

public sealed record OrderEditProofAddRequest(Guid? FileId, decimal Amount, DateOnly? PaidOn = null);

public sealed record OrderEditProofsRequest(
    IReadOnlyList<OrderEditProofAddRequest>? Add,
    IReadOnlyList<OrderPaymentProofUpdateRequest>? Update,
    IReadOnlyList<Guid>? RemoveIds);

/// <summary>El guardado atómico de «Editar» pedido (spec 2026-09-17). <c>IsRetail</c> y
/// <c>GlobalScaleFloor</c> son del pedido desde el spec 2026-10-15.</summary>
// Not sealed: PreviewOrderEditsRequest extends it with the preview-only billingCompanyId.
public record SaveOrderEditsRequest(
    IReadOnlyList<OrderEditItemRequest>? Items,
    OrderEditProofsRequest? Proofs,
    string? Notes,
    int? GlobalScaleFloor = null,
    bool IsRetail = false,
    /// <summary>El envío del pedido pendiente (spec 2026-10-17 §5.4). <b>Ausente deja el guardado</b>: un
    /// frontend viejo no lo borra. No cambia recoger o enviar: eso es <see cref="Delivery"/>, que se aplica antes,
    /// así que el envío se resuelve contra la recogida ya actualizada.</summary>
    ShippingRequest? Shipping = null,
    /// <summary>A quién se le factura (spec 2026-10-26). <b>Ausente deja la facturación</b>: un frontend viejo
    /// no la borra (Desvío 6). Presente, reemplaza el bloque entero: sin <c>Party</c> ni consumidor final
    /// factura a los datos del cliente.</summary>
    OrderBillingEditRequest? Billing = null,
    /// <summary>Los selectores de retención del pedido pendiente (spec impuestos por tipo D21, §9.3): ids de tasas
    /// activas, que retienen, del país de la empresa facturadora, una por tipo. <b>Ausente o null conserva</b> las del
    /// pedido; una lista vacía las quita. Reemplazan las del pedido, nunca las del cliente.</summary>
    IReadOnlyList<Guid>? WithholdingTaxRateIds = null,
    /// <summary>Fija o cambia la empresa y la cuenta con que se cobra el pedido pendiente (D22), misma forma que en la
    /// cotización. <b>Ausente o null conserva</b> la guardada. En otra moneda es
    /// <c>order.billing.currency_change_not_allowed</c> (plan P10).</summary>
    QuotationBillingAccountRequest? BillingAccount = null,
    /// <summary>El porcentaje de la retención municipal de cada tipo (spec ReteICA por empresa §7.7). Ausente conserva
    /// todos; una entrada con <c>percentage: null</c> vuelve al del catálogo.</summary>
    IReadOnlyList<WithholdingPercentageRequest>? WithholdingPercentages = null,
    /// <summary>Los tipos municipales con «Sin retención» elegido (owner 2026-11-13): quitan la fila de ese tipo del
    /// pedido. Ausente, null o vacío = sin marca.</summary>
    IReadOnlyList<string>? ClearedWithholdingTypes = null,
    /// <summary>La fecha del pedido (spec 2026-11-16 §4). <b>Ausente o null conserva</b> la guardada: un frontend viejo no
    /// la toca. No puede ser posterior a hoy en el calendario del tenant (<c>order.order_date.in_future</c>).</summary>
    DateOnly? OrderDate = null)
{
    /// <summary>
    /// El municipio de la operación (plan P1): ausente conserva el guardado (o el de la empresa si cambia, A6),
    /// <c>null</c> = el de la empresa, un id = ése. Ver <see cref="CreateQuotationRequest.OperationCityId"/> por qué es
    /// un <see cref="JsonElement"/> y una propiedad.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement OperationCityId { get; init; }

    /// <summary>Los regalos del pedido pendiente (spec 2026-11-23). <b>Ausente o null conserva</b> los guardados: un
    /// frontend viejo no los borra. Presente —también <c>[]</c>— es la lista <b>entera</b> deseada, por variante: agrega,
    /// cambia la cantidad y quita. <c>Items</c> sigue siendo sólo las líneas pagadas.</summary>
    public IReadOnlyList<OrderGiftLineRequest>? Gifts { get; init; }

    /// <summary>Recoger o enviar y a dónde (owner 2026-11-24, revierte H5 para la entrega). <b>Ausente conserva</b> la
    /// entrega guardada: un frontend viejo no la toca. Sólo con el pedido sin facturar y sin preparar. Es propiedad y no
    /// parámetro para que el cálculo previo la herede sin tocar su constructor.</summary>
    public OrderDeliveryEditRequest? Delivery { get; init; }
}

/// <summary>El bloque de entrega de «Editar pedido». BFF: es el mismo selector de «Nuevo pedido» —recoger en tienda o una
/// dirección—, así que viaja junto y con el mismo significado que en <see cref="QuotationPartiesRequest"/>.</summary>
/// <param name="IsStorePickup">Gana: a recoger se quita la dirección de envío y se limpia el envío, contraentrega incluida.</param>
/// <param name="Shipping">La parte de envío con datos propios; null = la dirección del cliente. Con recogida debe venir
/// null (<c>order.delivery.shipping_party_on_pickup</c>). La parte de envío no lleva documento: se ignora. Al guardar debe
/// venir completa (<c>order.shipping.party_incomplete</c>); el cálculo previo la acepta a medias.</param>
public sealed record OrderDeliveryEditRequest(bool IsStorePickup, QuotationPartyRequest? Shipping);

/// <summary>
/// The body of <c>POST /orders/{orderId}/preview</c>: the edit body plus <see cref="BillingCompanyId"/> (spec 2026-11-16
/// withholdings by company). Its own type so <c>PUT /orders/{orderId}</c> never learns the field.
/// </summary>
public sealed record PreviewOrderEditsRequest(
    IReadOnlyList<OrderEditItemRequest>? Items,
    OrderEditProofsRequest? Proofs,
    string? Notes,
    int? GlobalScaleFloor = null,
    bool IsRetail = false,
    ShippingRequest? Shipping = null,
    OrderBillingEditRequest? Billing = null,
    IReadOnlyList<Guid>? WithholdingTaxRateIds = null,
    QuotationBillingAccountRequest? BillingAccount = null,
    IReadOnlyList<WithholdingPercentageRequest>? WithholdingPercentages = null,
    IReadOnlyList<string>? ClearedWithholdingTypes = null,
    /// <summary>The billing company chosen before its bank account. Without <c>billingAccount</c>, absent still means
    /// «keep the saved account»; with this field it means «this company, account pending»: the preview resolves the
    /// order's withholdings against it and answers <c>billingAccount: null</c>.</summary>
    Guid? BillingCompanyId = null)
    : SaveOrderEditsRequest(
        Items, Proofs, Notes, GlobalScaleFloor, IsRetail, Shipping, Billing, WithholdingTaxRateIds, BillingAccount,
        WithholdingPercentages, ClearedWithholdingTypes);

/// <summary>El bloque de facturación de «Editar pedido»: la parte propia (o ninguna), el consumidor final,
/// la razón social del cliente y el perfil fiscal de la parte, como en <see cref="QuotationPartiesRequest"/>.</summary>
public sealed record OrderBillingEditRequest(
    QuotationPartyRequest? Party,
    bool BillsToFinalConsumer = false,
    bool BillingUsesBusinessName = false,
    /// <summary>Se ignora (spec impuestos por tipo §9.3): lo manda el frontend viejo. Las retenciones del pedido son
    /// <see cref="SaveOrderEditsRequest.WithholdingTaxRateIds"/>.</summary>
    bool? BillingWithRetention = null,
    bool? BillingVatSurplus = null);

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
    string FulfillmentStatus,
    bool Invoiced,
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
    DateTimeOffset? PaidMarkedAt,
    Guid? PaidMarkedBy,
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
    decimal FreightTaxAmount,
    /// <summary>Ver <see cref="OrderDto.Withholdings"/>.</summary>
    IReadOnlyList<WithholdingResponse> Withholdings,
    /// <summary>Siempre vacía en el pedido; ver <see cref="OrderDto.CustomerWithholdingTaxRates"/>.</summary>
    IReadOnlyList<WithholdingTaxRateResponse> CustomerWithholdingTaxRates,
    /// <summary>Las tasas vigentes del pedido; ver <see cref="OrderDto.BillingWithholdingTaxRates"/>.</summary>
    IReadOnlyList<WithholdingTaxRateResponse> BillingWithholdingTaxRates,
    DateTimeOffset? PreparedAt,
    Guid? PreparedBy,
    DateTimeOffset? ArchivedAt,
    Guid? ArchivedBy,
    /// <summary>Lo que falta por pagar: neto − consignado, con el redondeo de la moneda (pesos enteros en COP). Negativo
    /// es sobrante. Un total que no cierra se explica con un campo: la pantalla no resta comprobantes, que con la
    /// fracción de la retención le daría un saldo de centavos que no se ve (spec 2026-11-10 §3.2).</summary>
    decimal Balance,
    /// <summary>Ver <see cref="OrderDto.BuyerNotes"/>.</summary>
    string? BuyerNotes,
    /// <summary>Todas las tasas del pedido con su veredicto (spec retenciones por UVT §6), como en
    /// <see cref="QuotationResponse.WithholdingBreakdown"/>. Viaja en toda respuesta que trae <see cref="Withholdings"/>,
    /// también las de /approve, /cancel… (<see cref="IOrderResponseComposer.ComposeOrderAsync"/>).</summary>
    IReadOnlyList<WithholdingBreakdownResponse> WithholdingBreakdown,
    /// <summary>Ver <see cref="OrderDto.OrderDate"/>. Viaja como <c>"yyyy-MM-dd"</c>.</summary>
    DateOnly OrderDate,
    /// <summary>Si el pedido vende algo que se despacha (spec 2026-11-18 §5): alguna línea <c>Physical</c>. BFF: con false
    /// la pantalla oculta el envío y cambia «Preparar» por «Marcar como prestado» sin conocer el tipo de cada línea. El
    /// estado y la acción siguen siendo los de siempre (<c>Prepared</c>); el texto lo pone el frontend. Viaja acá y no en la
    /// raíz de <see cref="OrderDetailResponse"/>, para que también lo traigan /approve, /cancel… Con default para no tocar
    /// las construcciones que ya existen; el compositor lo llena siempre.</summary>
    bool RequiresShipping = true,
    /// <summary>Ver <see cref="OrderDto.InvoiceProvider"/>.</summary>
    string? InvoiceProvider = null,
    /// <summary>Ver <see cref="OrderDto.InvoiceNumber"/>.</summary>
    string? InvoiceNumber = null,
    /// <summary>Ver <see cref="QuotationResponse.PostalCodeSupported"/>. Viaja acá y no en la raíz de
    /// <see cref="OrderDetailResponse"/>, para que también lo traigan /approve, /cancel…</summary>
    bool PostalCodeSupported = true,
    /// <summary>BFF: el frontend sólo abre la entrega con <c>true</c>; ausente en un backend viejo = sólo lectura; no
    /// depende del origen (owner 2026-11-24). Es la guarda de edición del pedido, para que la pantalla no la deduzca de los
    /// estados. Viaja acá, como <see cref="PostalCodeSupported"/>, para que también lo traigan /approve, /cancel…</summary>
    bool DeliveryEditable = false);

/// <summary>Fila de la lista de pedidos. <c>QuotationId</c> y <c>QuotationNumber</c> son opcionales
/// desde el spec 2026-10-15: un pedido manual o externo no tiene cotización. <c>Source</c> y
/// <c>ExternalReference</c> van al final para que la tabla dibuje la insignia de origen y el
/// <c>#1001</c> debajo del número sin pedir el detalle. <c>FulfillmentStatus</c> e <c>Invoiced</c>, por lo mismo,
/// pintan las columnas de envío y facturado (spec 2026-11-10 §5).</summary>
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
    string FulfillmentStatus,
    bool Invoiced,
    string? PaymentMethod,
    DateTimeOffset ConvertedAt,
    string Currency,
    decimal Total,
    string Source,
    string? ExternalReference,
    /// <summary>La entrega por envío de la fila, misma forma que en el detalle (con el subtotal y el IVA del
    /// envío adentro): la grilla muestra transportadora y contraentrega sin pedir el pedido entero.</summary>
    ShippingResponse Shipping,
    /// <summary>La fecha del pedido (spec 2026-11-16 §4): la que filtra y ordena la lista.</summary>
    DateOnly OrderDate,
    /// <summary>Mismo campo y mismo significado que en <see cref="OrderResponse"/> (spec 2026-11-18 §5): la grilla dice
    /// «Prestado» en vez de «Preparado» sin pedir el detalle.</summary>
    bool RequiresShipping,
    /// <summary>Mayorista fase 3: número de la orden y aprobación; null fuera de un pedido mayorista.</summary>
    OrderListWholesaleDto? Wholesale = null);

/// <summary>La cotización de origen del detalle. <c>CreatedAt</c> es el de la cotización (spec 2026-11-16 F-8): el detalle
/// lo muestra junto a la fecha del pedido.</summary>
public sealed record SourceQuotationResponse(Guid Id, string QuotationNumber, DateTimeOffset CreatedAt);

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
    int[] AvailableFloors,
    // Spec 2026-11-10 §4.6: como en QuotationResponse. Sólo en la respuesta HTTP: el DTO interno no los trae.
    IReadOnlyList<ClientContactDto> ClientContacts,
    // Spec ReteICA por empresa §9.4 (plan P2, owner 2026-11-13): en la raíz y no dentro de Order, como Client y
    // AvailableFloors, porque los compone el endpoint con lookups que OrderResponse no tiene (OrderResponse también sale de
    // /approve, /cancel… sin compositor). Misma forma y misma regla que en QuotationResponse.
    QuotationCityResponse? OperationCity,
    QuotationCityResponse? BillingCompanyCity,
    IReadOnlyList<MunicipalWithholdingResponse> MunicipalWithholdings,
    // Spec 2026-11-15 §4: la cuenta de comprador del pedido de la tienda con su correo, para que la pantalla no consulte a
    // Buyers. Sólo en el detalle: el listado no hace la búsqueda cruzada. Null sin cuenta o con la cuenta ya borrada.
    BuyerAccountResponse? BuyerAccount,
    // Mayorista fase 3 (D-7): el distribuidor, la orden y la aprobación. Null fuera de un pedido mayorista.
    OrderWholesaleDto? Wholesale = null);

/// <summary>La cuenta de comprador de un pedido, como la muestra el detalle (spec 2026-11-15 §4).</summary>
public sealed record BuyerAccountResponse(Guid Id, string Email);

public sealed record OrdersPageResponse(
    IReadOnlyCollection<OrderListItemResponse> Items,
    int Total,
    int Page,
    int PageSize,
    /// <summary>Mayorista fase 3 (adenda): los pedidos mayoristas que esperan aprobación con los filtros vigentes salvo el de
    /// estado, para el aviso de la lista.</summary>
    int PendingWholesaleApprovals = 0);
