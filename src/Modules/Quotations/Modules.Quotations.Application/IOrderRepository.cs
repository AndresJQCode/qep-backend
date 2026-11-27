using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Posición de keyset para la exportación de pedidos (spec 2026-09-12, D8): el orden del
/// listado, fecha del pedido, instante de creación y número como desempate (spec 2026-11-16 §5).</summary>
public sealed record OrderExportCursor(DateOnly OrderDate, DateTimeOffset ConvertedAt, string OrderNumber);

public sealed record OrderExportPaymentProof(
    OrderPaymentProofId Id,
    string? PublicStorageKey,
    DateTimeOffset UploadedAt,
    decimal Amount,
    DateOnly? PaidOn = null);

/// <summary>El número de un pedido y su cliente, para quien sólo los va a mostrar.</summary>
public sealed record OrderNumberAndClient(string OrderNumber, Guid ClientId);

/// <summary>
/// Spec 2026-10-15 §6: el pedido se lista, filtra y exporta por sus propias columnas (cliente,
/// asesor, origen, totales), y sus líneas y partes son suyas. Ya no existe la fila
/// «pedido + cotización»: lo único que la cotización aporta es su número, para el enlace.
/// </summary>
public interface IOrderRepository
{
    Task<Order?> FindByQuotationIdAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken);

    Task<Order?> FindUntrackedByIdAsync(
        Guid tenantId, OrderId orderId, CancellationToken cancellationToken);

    /// <summary>Los pedidos de estas cotizaciones, por id de cotización: lo usa la lista de
    /// cotizaciones para mostrar si cada una tiene pedido y en qué estado.</summary>
    Task<IReadOnlyDictionary<Guid, Order>> FindByQuotationIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<QuotationId> quotationIds,
        CancellationToken cancellationToken);

    Task<Order?> FindByIdAsync(
        Guid tenantId, OrderId orderId, CancellationToken cancellationToken);

    /// <summary>El número (<c>PED-…</c>) de estos pedidos, en una sola consulta: lo usa la bandeja de Shopify para mostrarlo en
    /// cada fila importada de una página (spec 2026-10-15 §7). Un id que no es del tenant no aparece.</summary>
    Task<IReadOnlyDictionary<OrderId, string>> FindOrderNumbersAsync(
        Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken cancellationToken);

    /// <summary>El número y el cliente de estos pedidos, en una sola consulta y sin cargarlos: lo usan las tandas de facturación
    /// (spec 2026-11-24 §5). Un id que no es del tenant no aparece.</summary>
    Task<IReadOnlyDictionary<OrderId, OrderNumberAndClient>> FindNumbersAndClientsAsync(
        Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken cancellationToken);

    /// <summary>De los ids dados, cuáles requieren envío (spec 2026-11-18 §5), con la regla de
    /// <see cref="Order.RequiresShipping"/>. Para el listado, que no trae las líneas. Un id que no es del tenant no
    /// aparece.</summary>
    Task<IReadOnlySet<Guid>> FindIdsRequiringShippingAsync(
        Guid tenantId, IReadOnlyCollection<OrderId> orderIds, CancellationToken cancellationToken);

    /// <summary>El pedido externo con esta clave de idempotencia (spec de Shopify D-S23), o null. La clave es
    /// única por <c>(tenant, source)</c>: la misma clave en otra plataforma es otro pedido.</summary>
    Task<Order?> FindByExternalKeyAsync(
        Guid tenantId, OrderSource source, string externalKey, CancellationToken cancellationToken);

    /// <summary>Cuántos pedidos de ese origen entraron desde <paramref name="since"/> (por <c>ConvertedAt</c>), en
    /// cualquier estado: el tope diario de la tienda cuenta también los anulados.</summary>
    Task<int> CountBySourceSinceAsync(
        Guid tenantId, OrderSource source, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// La página del listado. Sin <paramref name="status"/> no trae los archivados (spec 2026-11-10 §5, como Shopify): para
    /// verlos se filtra <c>Archived</c>. La exportación (<see cref="AnyForExportAsync"/>, <see cref="ListForExportAsync"/>)
    /// sigue la misma regla, porque el archivo es «lo que estoy viendo». <paramref name="invoiced"/> mira la marca de
    /// facturado (<c>InvoicedAt</c>), que un pedido archivado conserva.
    /// </summary>
    Task<(IReadOnlyList<Order> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        OrderFulfillmentStatus? fulfillmentStatus,
        bool? invoiced,
        DateOnly? orderDateFrom,
        DateOnly? orderDateTo,
        string? orderNumber,
        OrderSource? source,
        QuotationKind? kind,
        bool? wholesaleApproved,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Mayorista fase 3 (adenda del contrato): cuántos pedidos mayoristas abiertos esperan aprobación con los mismos filtros
    /// que la página, salvo el de estado (y el de aprobación, que es justo lo que se cuenta). Alimenta el aviso «N pedidos de
    /// distribuidores esperan tu aprobación».
    /// </summary>
    Task<int> CountAwaitingWholesaleApprovalAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderPaymentStatus? paymentStatus,
        OrderFulfillmentStatus? fulfillmentStatus,
        bool? invoiced,
        DateOnly? orderDateFrom,
        DateOnly? orderDateTo,
        string? orderNumber,
        OrderSource? source,
        QuotationKind? kind,
        CancellationToken cancellationToken);

    /// <summary>Los pedidos de un comprador de la tienda, del más reciente al más viejo, sin tracking.</summary>
    Task<(IReadOnlyList<Order> Items, int Total)> SearchByBuyerAsync(
        Guid tenantId, Guid buyerAccountId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// Spec 2026-11-15 §3.2: en una sola sentencia, pone <paramref name="buyerAccountId"/> en los pedidos del tenant de origen
    /// tienda que no tienen cuenta ni decisión de un asesor y cuyo correo de facturación, recortado y en minúsculas, es
    /// <paramref name="normalizedEmail"/>. Las condiciones van en el <c>WHERE</c> para no pisar una escritura concurrente del
    /// asesor. No sube la versión (como <see cref="Order.LinkBuyerAccount"/>). Devuelve las filas enlazadas.
    /// </summary>
    Task<int> LinkStoreOrdersByBillingEmailAsync(
        Guid tenantId, Guid buyerAccountId, string normalizedEmail, CancellationToken cancellationToken);

    /// <summary>
    /// Obliga a que el siguiente guardado escriba <c>buyer_account_id</c> aunque el valor no haya cambiado respecto a lo
    /// leído. Lo necesita la decisión del asesor (spec 2026-11-15 §4): los enlaces automáticos (<see cref="Order.LinkBuyerAccount"/>
    /// y <see cref="LinkStoreOrdersByBillingEmailAsync"/>) no suben la versión, así que uno que confirme entre la carga y el
    /// guardado pasa el <c>WHERE version = …</c>; si quitar sobre un pedido cargado sin cuenta no escribiera la columna, la
    /// fila quedaría con la cuenta automática y la marca del asesor a la vez.
    /// </summary>
    void ForceBuyerAccountWrite(Order order);

    /// <summary>Un pedido del comprador, con tracking y con sus comprobantes, para poder cancelarlo.</summary>
    Task<Order?> FindByBuyerAsync(
        Guid tenantId, Guid buyerAccountId, OrderId orderId, CancellationToken cancellationToken);

    Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        OrderFulfillmentStatus? fulfillmentStatus,
        bool? invoiced,
        DateOnly? orderDateFrom,
        DateOnly? orderDateTo,
        string? orderNumber,
        OrderSource? source,
        QuotationKind? kind,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Order>> ListForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        OrderFulfillmentStatus? fulfillmentStatus,
        bool? invoiced,
        DateOnly? orderDateFrom,
        DateOnly? orderDateTo,
        string? orderNumber,
        OrderSource? source,
        QuotationKind? kind,
        OrderExportCursor? after,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>>> ListPaymentProofsForExportAsync(
        Guid tenantId,
        IReadOnlyCollection<OrderId> orderIds,
        CancellationToken cancellationToken);

    /// <summary>Las líneas de estos pedidos, por pedido y en su posición: lo que el Excel recorre.</summary>
    Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderItem>>> ListItemsForExportAsync(
        Guid tenantId,
        IReadOnlyCollection<OrderId> orderIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<OrderId, IReadOnlyList<OrderParty>>> ListPartiesForExportAsync(
        Guid tenantId,
        IReadOnlyCollection<OrderId> orderIds,
        CancellationToken cancellationToken);

    /// <summary>El historial del pedido, del más reciente al más viejo (spec 2026-10-15 §4.6).</summary>
    Task<IReadOnlyList<OrderHistoryEntry>> ListHistoryAsync(
        Guid tenantId, OrderId orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Cuáles de <paramref name="fileIds"/> ya usa algún comprobante de pago, menos <paramref name="exceptProofId"/>
    /// (revisión final, I2). Una sola lectura para todos los archivos de un cuerpo.
    /// </summary>
    Task<IReadOnlySet<Guid>> ListPaymentProofFilesInUseAsync(
        IReadOnlyCollection<Guid> fileIds, OrderPaymentProofId? exceptProofId, CancellationToken cancellationToken);

    void Add(Order order);

    void AddHistoryEntry(OrderHistoryEntry entry);
}
