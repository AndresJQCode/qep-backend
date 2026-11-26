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
/// <see cref="HeldPushCount"/> (owner 2026-11-14) son los pedidos de la tienda con correcciones retenidas (envío <c>Held</c>), en cero
/// si no hay. BFF: la pantalla pinta «N pedidos pendientes de sincronizar» junto al botón que los suelta
/// (<c>POST …/stores/{connectionId}/pushes/release</c>) sin pedir la lista de envíos.
/// </summary>
public sealed record ShopifyStoreSummaryDto(
    Guid ConnectionId,
    string Name,
    string? ShopDomain,
    string Status,
    bool IsConfigured,
    int WaitingCount,
    DateTimeOffset? LastReconciledAt,
    int HeldPushCount);

/// <summary>
/// Spec 2026-10-15 §6.1 y §7 <c>GET|PUT /stores/{connectionId}</c>: la configuración de la tienda. BFF: el formulario muestra el
/// asesor, la empresa y la clasificación elegidos por su nombre sin consultar Tenancy, Companies ni Customers, así que viajan
/// <see cref="AdvisorName"/>, <see cref="BillingCompanyName"/> y <see cref="ClassificationName"/> junto a sus ids (cada nombre es
/// <c>null</c> si su id lo es, o si ya no existe). <see cref="Version"/> es lo que el formulario manda en <c>If-Match</c>; la misma
/// versión sale en el <c>ETag</c>. Una tienda sin configurar trae los ids en <c>null</c> y las claves por defecto
/// (<c>Documento</c>, <c>Tipo de documento</c>, <c>CC</c>), que es lo que el formulario pinta de entrada.
/// <see cref="HistoryDays"/> (spec 2026-10-21) son los días antes del enlace que trae «Sincronizar ahora»; <c>null</c> es sin
/// historial, y viaja siempre, también en <c>null</c>, para que el campo del formulario arranque vacío.
/// <see cref="EditSync"/> (<c>Manual</c> | <c>Automatic</c>) y <see cref="EditPaymentStatuses"/> (nombres de los estados de pago
/// del pedido de QEP con los que se admite un cambio de Shopify) son del spec 2026-11-14 y viajan siempre, con sus valores por
/// defecto en una tienda sin configurar, para que el formulario no tenga que conocerlos.
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
    int? HistoryDays,
    string EditSync,
    IReadOnlyList<string> EditPaymentStatuses,
    DateTimeOffset? LastReconciledAt,
    DateTimeOffset UpdatedAt,
    long Version);

/// <summary>
/// <c>GET /stores/{connectionId}/document-sources</c>: las opciones del select «Atributo del documento», en el orden en que se
/// dibujan. BFF: el select no puede saber qué atributos manda el checkout de la tienda (eso sólo lo ve el espejo de pedidos) ni
/// cómo los resuelve el importador, así que cada valor ya es una clave que el <c>PUT</c> de la tienda acepta y
/// <see cref="ShopifyDocumentSources.Read"/> resuelve, tal cual va en <c>documentAttributeKey</c>: primero las rutas
/// <c>shipping_address.company</c> y <c>billing_address.company</c> (siempre, aunque la tienda no tenga pedidos; la de facturación
/// desde el spec 2026-11-14) y después los nombres de atributo, sin repetir y ordenados. El texto de cada opción lo pone el frontend: la ruta tiene su etiqueta en el diccionario y un atributo se muestra por
/// su nombre. Va envuelto en un objeto y no como arreglo suelto para poder sumar campos sin romper el contrato. La clave guardada
/// en la tienda no se agrega si ya no aparece en los pedidos: la trae <c>GET /stores/{connectionId}</c>.
/// </summary>
public sealed record ShopifyDocumentSourcesDto(IReadOnlyList<string> Sources);

/// <summary>
/// Spec 2026-10-15 §5.4 y §7: una fila de la bandeja, igual en la lista, en el reintento y en «Marcar como revisado» (las dos
/// acciones devuelven la fila para que la pantalla la repinte sin volver a pedir la página). Formas que el frontend tiene que
/// leer tal cual:
/// <list type="bullet">
/// <item><see cref="ImportState"/> viaja con su nombre (<c>Waiting</c>, <c>Imported</c>, <c>Cancelled</c>, <c>Ignored</c>), y
/// <see cref="WaitReason"/> con el código de §6.2 (<c>store_not_configured</c>, …, <c>order_rejected</c>); los textos los tiene
/// el diccionario del frontend.</item>
/// <item><see cref="WaitDetail"/> es el JSON que guardó el importador, o <c>null</c>. Con <c>product_unmatched</c> es
/// <c>{"skus":[…],"customTitles":[…]}</c>, las dos llaves siempre presentes: <c>skus</c> trae los SKU sin resolver (una línea sin SKU
/// llega como <c>null</c> dentro del arreglo: <c>{"skus":[null],"customTitles":[]}</c>) y <c>customTitles</c> los nombres de líneas
/// personalizadas sin asociación, que la bandeja ofrece asociar a un producto. Con
/// <c>currency_mismatch</c>, <c>{"orderCurrency","billingCurrency"}</c>; con <c>customer_inactive</c>, <c>{"customerId"}</c>;
/// con <c>order_rejected</c>, <c>{"code"}</c>; con los del envío, <c>{"shippingTitle","amount"}</c>. Una fila
/// <c>Cancelled</c> después de facturar trae <c>{"reason":"invoiced_before_cancel"}</c>.</item>
/// <item><see cref="Warnings"/> es un arreglo <c>[{"code","detail"}]</c> o <c>null</c> sin avisos, y <c>detail</c> puede ser
/// <c>null</c> (<c>shipping_address_unresolved</c>, <c>shipping_region_unresolved</c> y <c>total_difference</c> no llevan detalle: la
/// diferencia está en
/// <see cref="TotalDifference"/>). <see cref="WarningsReviewedAt"/> <c>null</c> es aviso pendiente (D-S31).</item>
/// <item><see cref="LastError"/> es texto libre para soporte y logs: puede traer varios errores unidos con <c>"; "</c>, nombres
/// de enum o un <c>invalid_payload: …</c> en inglés. La pantalla no lo muestra.</item>
/// <item><see cref="LastErrorCodes"/> es lo que la bandeja pinta: los códigos de <see cref="LastError"/> normalizados por
/// <see cref="ShopifyLastErrorCodes.Normalize"/>, en orden y sin repetir, y <c>[]</c> sin error (siempre presente). BFF: existe
/// porque el formato de <c>last_error</c> es del backend; si la pantalla lo partiera, tendría que conocer el separador, los
/// prefijos y los nombres de enum de cada origen, y un cambio aquí rompería la traducción en silencio. Cada código es uno de
/// Quotations tal cual (<c>order.order.not_editable</c>, …), <c>shopify.order.invalid_payload</c> o
/// <c>shopify.order.sync_failed</c> (todo lo que no es código); el texto lo tiene el diccionario del frontend.</item>
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
    IReadOnlyList<string> LastErrorCodes,
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
/// en cualquier estado posterior; <see cref="ChangesToReview"/>, los cambios de Shopify <c>Pending</c> y <c>Blocked</c> (spec
/// 2026-11-14 §6, la sección «Cambios por revisar»).
/// </summary>
public sealed record ShopifyOrdersSummaryDto(int Waiting, int WithWarnings, int CancelledAfterInvoice, int ImportedToday, int ChangesToReview);

/// <summary>
/// Spec 2026-11-14 §6: un cambio de Shopify como lo pinta la bandeja («Cambios por revisar») y el detalle del pedido.
/// <para>BFF: <see cref="Groups"/> viaja armado (grupo, decisión, motivo y filas de antes y después con textos, números crudos y
/// booleanos), porque el frontend no conoce el diff de Shopify ni los enums de QEP; sólo los pinta. Los enums (<see cref="Status"/>,
/// <c>kind</c>, <c>outcome</c>) van por nombre y los motivos (<see cref="StatusReason"/>, <c>reason</c>) en <c>snake_case</c>, que el
/// frontend traduce con su diccionario. La excepción es un <c>Pending</c> que la aplicación automática no pudo aplicar por una regla
/// del pedido (review M-2): su <see cref="StatusReason"/> es el código de Quotations tal cual (por ejemplo
/// <c>order.order.store_pickup</c>), el mismo que respondería aceptarlo. Los montos van como número crudo en <see cref="Currency"/> (plan D19): el frontend ya
/// formatea por moneda, y un texto armado acá fijaría el formato regional en dos lugares.</para>
/// <para><see cref="Currency"/> es la del pedido de QEP: sale de <c>shopify.orders.currency</c>, que la importación exige igual a la
/// de la cuenta de cobro (<c>currency_mismatch</c>); siempre presente. <see cref="DecidedByName"/> es el nombre de quien aceptó,
/// rechazó o revisó; <c>null</c> si lo decidió el sistema.</para>
/// </summary>
public sealed record ShopifyOrderChangeDto(
    Guid Id,
    Guid MirrorOrderId,
    Guid ConnectionId,
    string? StoreName,
    string OrderName,
    Guid? QepOrderId,
    string? QepOrderNumber,
    string Currency,
    string Status,
    string? StatusReason,
    DateTimeOffset? BaseUpdatedAt,
    DateTimeOffset SnapshotUpdatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    string? DecidedByName,
    JsonElement Groups);

/// <summary>Spec 2026-11-14 §6 <c>GET /changes</c>: la página y el total del filtro, como <see cref="ShopifyOrderPageDto"/>.</summary>
public sealed record ShopifyOrderChangePageDto(IReadOnlyList<ShopifyOrderChangeDto> Items, int Total, int Page, int PageSize);

/// <summary>Spec 2026-11-14 §5: un atributo de Shopify (<c>note_attributes</c>) como lo edita el detalle del pedido.</summary>
public sealed record ShopifyAttributeDto(string Key, string Value);

/// <summary>Spec 2026-11-14 §5: el último envío a Shopify del pedido. <see cref="Status"/> (<c>Pending</c>, <c>Sent</c>,
/// <c>Failed</c>, <c>Held</c>) y <see cref="Groups"/> (<c>ShippingAddress</c>, <c>Note</c>, <c>Attributes</c>) por nombre;
/// <see cref="FailureCode"/> en <c>snake_case</c>, que el frontend traduce: en <c>Failed</c>, <c>user_error</c>,
/// <c>order_not_found</c>, <c>rejected</c> o <c>retries_exhausted</c>; en <c>Held</c> (owner 2026-11-14, retenido hasta que alguien
/// lo sincronice), el motivo: <c>connection_inactive</c>, <c>connection_missing</c> o <c>access_denied</c>. <see cref="LastAttemptAt"/> es la
/// última vez que el worker lo tocó, <c>null</c> si todavía no lo intentó.</summary>
public sealed record ShopifyOrderPushDto(
    string Status, IReadOnlyList<string> Groups, string? FailureCode, int Attempts, DateTimeOffset? LastAttemptAt, DateTimeOffset? SentAt);

/// <summary>
/// Spec 2026-11-14 §5 y §6: el panel de Shopify en el detalle del pedido de QEP (plan D11). BFF: la pantalla no conoce la regla de
/// edición de Quotations ni la fila del espejo, así que todo lo que decide qué se muestra y qué se deja editar viene armado acá.
/// <para><see cref="CanEdit"/>: la fila está <c>Imported</c> y el pedido es editable. <see cref="EditBlockedReason"/> (plan D20) es
/// <c>null</c> si y sólo si <see cref="CanEdit"/>; si no, el primero que aplique en este orden: <c>NotImported</c> (la fila no está
/// importada o no tiene pedido), <c>Cancelled</c>, <c>Archived</c>, <c>Invoiced</c>, <c>Prepared</c>, que son los estados que el dominio
/// distingue en <c>Order.IsEditable</c>. Así la pantalla dice por qué no se puede editar sin repetir la regla.</para>
/// <para><see cref="ShippingAddressSupported"/> es <c>false</c> con recogida en tienda o con una dirección fuera de Colombia (plan
/// D21): la pantalla manda entonces <c>shippingParty: null</c>, y nota y atributos siguen editables.</para>
/// <para><see cref="Attributes"/> son los de QEP si el asesor los corrigió (<see cref="AttributesEditedInQep"/>); si no, los del
/// último Shopify aplicado. <see cref="OrderVersion"/> es el <c>If-Match</c> del <c>PUT …/corrections</c>. <see cref="Push"/> es el
/// último envío (<c>null</c> si nunca hubo) y <see cref="OpenChange"/>, el cambio <c>Pending</c> o <c>Blocked</c> de Shopify.</para>
/// <para><see cref="ShopifySync"/> (owner 2026-11-14) sólo viene en la respuesta del <c>PUT …/corrections</c>; en el <c>GET</c> y en
/// <c>POST …/push</c> es <c>null</c>. Dice qué pasó con la corrección del lado de Shopify: <c>Queued</c> (encolada, sale sola),
/// <c>Held</c> (guardada en QEP pero retenida: la conexión no está activa, o ya había un envío retenido de este pedido) o
/// <c>NothingToSend</c> (no cambió nada). BFF: con <c>Held</c> la pantalla avisa «la conexión no está activa; el cambio queda
/// marcado y lo podrás sincronizar cuando la actives» sin tener que cruzar el estado de la conexión con el del envío.</para>
/// </summary>
public sealed record ShopifyOrderSyncDto(
    Guid MirrorOrderId,
    Guid ConnectionId,
    string OrderName,
    long OrderVersion,
    bool CanEdit,
    string? EditBlockedReason,
    bool ShippingAddressSupported,
    IReadOnlyList<ShopifyAttributeDto> Attributes,
    bool AttributesEditedInQep,
    ShopifyOrderPushDto? Push,
    ShopifyOrderChangeDto? OpenChange,
    string? ShopifySync = null);

/// <summary>Owner 2026-11-14: lo que respondió soltar los envíos retenidos de una tienda: cuántos quedaron pendientes.</summary>
public sealed record ShopifyPushReleaseResult(int Released);
