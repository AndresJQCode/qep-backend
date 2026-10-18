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

    /// <summary>El pedido externo con esta clave de idempotencia (spec de Shopify D-S23), o null. La clave es
    /// única por <c>(tenant, source)</c>: la misma clave en otra plataforma es otro pedido.</summary>
    Task<Order?> FindByExternalKeyAsync(
        Guid tenantId, OrderSource source, string externalKey, CancellationToken cancellationToken);

    Task<(IReadOnlyList<Order> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
        DateTimeOffset? convertedFrom,
        DateTimeOffset? convertedBefore,
        string? orderNumber,
        OrderSource? source,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<bool> AnyForExportAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        OrderStatus? status,
        OrderPaymentStatus? paymentStatus,
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
