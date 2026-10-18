using System.Text.Json;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §7 <c>GET /stores</c>: una fila por conexión de Shopify del tenant, aunque el módulo todavía no tenga su
/// tienda (§6.1: la tienda nace en el primer contacto). BFF: la pantalla de tiendas dibuja la lista sin una segunda llamada por
/// tienda, así que viajan juntos lo de Integrations (<see cref="Name"/>, <see cref="Status"/>) y lo del módulo
/// (<see cref="IsConfigured"/>, <see cref="WaitingCount"/>, <see cref="LastReconciledAt"/>). <see cref="Status"/> es el nombre del
/// estado de la conexión (<c>Active</c>, <c>Paused</c>, <c>NeedsAttention</c>); el texto lo tiene el frontend.
/// <see cref="WaitingCount"/> va en cero, nunca ausente. <see cref="LastReconciledAt"/> es <c>null</c> mientras la tienda no
/// exista en el módulo. <see cref="ShopDomain"/> es <c>null</c> si no se conoce, igual que en <see cref="ShopifyStoreDto"/>.
/// </summary>
public sealed record ShopifyStoreSummaryDto(
    Guid ConnectionId,
    string Name,
    string? ShopDomain,
    string Status,
    bool IsConfigured,
    int WaitingCount,
    DateTimeOffset? LastReconciledAt);

/// <summary>
/// Spec 2026-10-15 §6.1 y §7 <c>GET|PUT /stores/{connectionId}</c>: la configuración de la tienda. BFF: el formulario muestra el
/// asesor, la empresa y la clasificación elegidos por su nombre sin consultar Tenancy, Companies ni Customers, así que viajan
/// <see cref="AdvisorName"/>, <see cref="BillingCompanyName"/> y <see cref="ClassificationName"/> junto a sus ids (cada nombre es
/// <c>null</c> si su id lo es, o si ya no existe). <see cref="Version"/> es lo que el formulario manda en <c>If-Match</c>; la misma
/// versión sale en el <c>ETag</c>. Una tienda sin configurar trae los ids en <c>null</c> y las claves por defecto
/// (<c>Documento</c>, <c>Tipo de documento</c>, <c>CC</c>), que es lo que el formulario pinta de entrada.
/// </summary>
public sealed record ShopifyStoreDto(
    Guid ConnectionId,
    string? ShopDomain,
    bool IsConfigured,
    Guid? AdvisorMembershipId,
    string? AdvisorName,
    Guid? BillingCompanyId,
    string? BillingCompanyName,
    string? BillingBankName,
    string? BillingAccountNumber,
    string? BillingCurrency,
    Guid? DefaultClassificationId,
    string? ClassificationName,
    string DocumentAttributeKey,
    string DocumentTypeAttributeKey,
    string DefaultIdentificationType,
    DateTimeOffset? LastReconciledAt,
    DateTimeOffset UpdatedAt,
    long Version);

/// <summary>
/// Spec 2026-10-15 §5.4 y §7: una fila de la bandeja, igual en la lista, en el reintento y en «Marcar como revisado» (las dos
/// acciones devuelven la fila para que la pantalla la repinte sin volver a pedir la página). Formas que el frontend tiene que
/// leer tal cual:
/// <list type="bullet">
/// <item><see cref="ImportState"/> viaja con su nombre (<c>Waiting</c>, <c>Imported</c>, <c>Cancelled</c>, <c>Ignored</c>), y
/// <see cref="WaitReason"/> con el código de §6.2 (<c>store_not_configured</c>, …, <c>order_rejected</c>); los textos los tiene
/// el diccionario del frontend.</item>
/// <item><see cref="WaitDetail"/> es el JSON que guardó el importador, o <c>null</c>. Con <c>product_unmatched</c> es
/// <c>{"skus":[…]}</c>, y una línea sin SKU llega como <c>null</c> dentro del arreglo: <c>{"skus":[null]}</c>. Con
/// <c>currency_mismatch</c>, <c>{"orderCurrency","billingCurrency"}</c>; con <c>customer_inactive</c>, <c>{"customerId"}</c>;
/// con <c>order_rejected</c>, <c>{"code"}</c>; con los del envío, <c>{"shippingTitle","amount"}</c>. Una fila
/// <c>Cancelled</c> después de facturar trae <c>{"reason":"invoiced_before_cancel"}</c>.</item>
/// <item><see cref="Warnings"/> es un arreglo <c>[{"code","detail"}]</c> o <c>null</c> sin avisos, y <c>detail</c> puede ser
/// <c>null</c> (<c>shipping_address_unresolved</c> y <c>total_difference</c> no llevan detalle: la diferencia está en
/// <see cref="TotalDifference"/>). <see cref="WarningsReviewedAt"/> <c>null</c> es aviso pendiente (D-S31).</item>
/// <item><see cref="LastError"/> puede traer varios errores unidos con <c>"; "</c> (p. ej. el rechazo del pago y el de la
/// anulación del mismo aviso); se muestra como texto, no se parte.</item>
/// <item><see cref="StoreName"/> es el nombre de la conexión, <c>null</c> si ya no existe. <see cref="CustomerName"/> y
/// <see cref="CustomerEmail"/> son los del comprador en el último snapshot de Shopify (no los de la ficha de QEP), <c>null</c> en
/// un pedido sin cliente. <see cref="QepOrderNumber"/> es el <c>PED-…</c> del pedido de QEP, <c>null</c> mientras no haya
/// pedido.</item>
/// </list>
/// </summary>
public sealed record ShopifyOrderRowDto(
    Guid Id,
    Guid ConnectionId,
    string OrderName,
    string? StoreName,
    DateTimeOffset ShopifyCreatedAt,
    string? FinancialStatus,
    decimal TotalPrice,
    string Currency,
    string ImportState,
    string? WaitReason,
    JsonElement? WaitDetail,
    JsonElement? Warnings,
    DateTimeOffset? WarningsReviewedAt,
    int Attempts,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    Guid? QepCustomerId,
    Guid? QepOrderId,
    string? QepOrderNumber,
    decimal? TotalDifference,
    string? CustomerName,
    string? CustomerEmail);

/// <summary>§7 <c>GET /orders</c>: la página y el total del filtro, para que la pantalla pinte el paginador sin contar.</summary>
public sealed record ShopifyOrderPageDto(IReadOnlyList<ShopifyOrderRowDto> Items, int Total, int Page, int PageSize);

/// <summary>
/// Spec 2026-10-15 §7 <c>GET /orders/summary</c>: los contadores de las pestañas de la bandeja. BFF: viajan los cuatro
/// <b>siempre, incluso en cero</b> (como <c>ReportStatusSliceDto</c>): uno que falte obligaría a la pantalla a saber cuáles hay.
/// Es un endpoint y no una suma del cliente porque la lista está paginada (tope 50). <see cref="WithWarnings"/> cuenta sólo los
/// avisos sin revisar; <see cref="CancelledAfterInvoice"/>, las filas <c>Cancelled</c> con <c>invoiced_before_cancel</c> (hay
/// que revertir la factura a mano); <see cref="ImportedToday"/>, las filas importadas desde la medianoche en el huso del tenant,
/// en cualquier estado posterior.
/// </summary>
public sealed record ShopifyOrdersSummaryDto(int Waiting, int WithWarnings, int CancelledAfterInvoice, int ImportedToday);
