namespace Modules.Customers.Application;

/// <summary>
/// Spec 2026-11-10 (borrar clientes) §4.3 y A-8: lo que el borrado se llevó de otros módulos por cliente, para la foto de la
/// auditoría.
/// </summary>
public sealed record CustomerDeletionCounts(int Conversations, int Messages, int Deals, int PosSales, int CampaignRecipients);

/// <summary>
/// Lo que devuelve <see cref="ICustomerDeletionReferences.RemoveAsync"/>: los conteos de cada cliente y las claves de R2 de
/// los adjuntos que quedaron sin dueño (A-9), que se purgan después del commit por el outbox.
/// </summary>
public sealed record CustomerDeletionOutcome(
    IReadOnlyDictionary<Guid, CustomerDeletionCounts> Counts,
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> StorageKeys);

/// <summary>
/// Spec 2026-11-10 §4.2, §4.3 y §5: lo que el borrado lee y limpia fuera de Customers. Customers no referencia los otros
/// módulos (<c>CustomersLayerTests</c>): el adaptador vive en Bootstrapper, como el de <see cref="ICustomerMergeReferences"/>.
/// Todo corre en la conexión y la transacción del <c>CustomersDbContext</c> del scope y recibe <c>tenantId</c>; sin ids no
/// consulta.
/// </summary>
public interface ICustomerDeletionReferences
{
    /// <summary>
    /// Toma el candado consultivo exclusivo de cada cliente, ordenado por id para que dos borrados no se crucen. Va antes de
    /// leer los bloqueos: quien crea una cotización, un pedido, un préstamo, un contrato o una conversación toma el mismo
    /// candado compartido, así que lo que se lee después ya no cambia hasta el commit.
    /// </summary>
    Task LockAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Sólo los clientes bloqueados, con sus motivos en el orden de <see cref="CustomerDeletionBlockReasons.Ordered"/>. Un
    /// cliente sin bloqueos no aparece.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> FindBlockersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Borra o desliga lo de otros módulos según la tabla de §4.3 y cuenta lo que se llevó.</summary>
    Task<CustomerDeletionOutcome> RemoveAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
