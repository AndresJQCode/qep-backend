using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Posición de keyset para la exportación de pedidos (spec 2026-09-12, D8): el orden del
/// listado, fecha de conversión y número como desempate.</summary>
public sealed record OrderExportCursor(DateTimeOffset ConvertedAt, string OrderNumber);

public sealed record OrderExportPaymentProof(
    OrderPaymentProofId Id,
    string? PublicStorageKey,
    DateTimeOffset UploadedAt,
    decimal Amount,
    DateOnly? PaidOn = null);

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
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
        string? orderNumber,
        OrderSource? source,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Los pedidos de un comprador de la tienda, del más reciente al más viejo, sin tracking.</summary>
    Task<(IReadOnlyList<Order> Items, int Total)> SearchByBuyerAsync(
        Guid tenantId, Guid buyerAccountId, int page, int pageSize, CancellationToken cancellationToken);

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
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
        string? orderNumber,
        OrderSource? source,
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
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
        string? orderNumber,
        OrderSource? source,
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

    Task<bool> IsPaymentProofFileInUseAsync(
        Guid fileId, OrderPaymentProofId? exceptProofId, CancellationToken cancellationToken);

    void Add(Order order);

    void AddHistoryEntry(OrderHistoryEntry entry);
}
