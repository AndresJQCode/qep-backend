namespace Modules.Shopify.Application;

/// <summary>Guarda las tiendas, el espejo, las entregas y su auditoría en una sola transacción. La implementación traduce los
/// índices únicos por nombre y la concurrencia, en Infrastructure (Application no referencia EF Core ni Npgsql).</summary>
public interface IShopifyUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Olvida lo cargado y lo pendiente. Tras perder una carrera (<c>concurrency.conflict</c>) la entidad en memoria tiene
    /// la versión vieja: sin esto, volver a leerla devolvería la misma instancia y el siguiente guardado chocaría otra vez.</summary>
    void DiscardPendingChanges();

    /// <summary>Abre una transacción explícita sobre la conexión del módulo; <see cref="SaveChangesAsync"/> se suma a ella. Hace
    /// falta para <see cref="LockCitySynonymAsync"/> y para el candado de la fila de un trabajo de corrección.</summary>
    Task<IShopifyTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Spec 2026-10-26 D-C6: <c>pg_advisory_xact_lock(hashtextextended('shopify.city-synonym:' || synonym_id, 0))</c>. Exige una
    /// transacción abierta con <see cref="BeginTransactionAsync"/>: el candado se suelta con ella. Serializa escribir una huella
    /// del sinónimo (el worker que corrige) con listar sus huellas para recorregirlas (el evento <c>corrected</c>): o la huella se
    /// guardó antes y la lista la ve, o el worker relee el sinónimo después y ve la ciudad nueva.
    /// </summary>
    Task LockCitySynonymAsync(Guid synonymId, CancellationToken cancellationToken);
}

/// <summary>Se confirma con <see cref="CommitAsync"/>; disponerla sin commit hace rollback, que además suelta los candados
/// que haya tomado.</summary>
public interface IShopifyTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
