using Modules.Store.Domain;

namespace Modules.Store.Application;

public interface IStoreOrderRequestRepository
{
    void Add(StoreOrderRequest request);

    /// <summary>Rastreada y con sus líneas.</summary>
    Task<StoreOrderRequest?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);

    /// <summary>La reintentada por el mismo visitante: el manejador público la relee tras un duplicado.</summary>
    Task<StoreOrderRequest?> FindByClientRequestIdAsync(Guid tenantId, Guid clientRequestId, CancellationToken cancellationToken);

    /// <summary>Sin rastrear y con sus líneas (la bandeja cuenta productos y unidades). Del más reciente al más viejo.</summary>
    Task<(IReadOnlyList<StoreOrderRequest> Items, int Total)> ListAsync(StoreInboxFilter filter, CancellationToken cancellationToken);

    Task<StoreInboxCounts> CountAsync(
        Guid tenantId,
        Guid callerMemberId,
        DateTimeOffset todayStartUtc,
        DateTimeOffset todayEndUtc,
        CancellationToken cancellationToken);

    /// <summary>Cuántas llegaron desde <paramref name="since"/>; es el tope diario del tenant.</summary>
    Task<int> CountReceivedSinceAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>La solicitud de la que salió ese pedido; null para pedidos de otro origen.</summary>
    Task<StoreOrderRequest?> FindByOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
}
