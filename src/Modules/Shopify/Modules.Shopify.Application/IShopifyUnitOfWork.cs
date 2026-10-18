namespace Modules.Shopify.Application;

/// <summary>Guarda las tiendas, el espejo, las entregas y su auditoría en una sola transacción. La implementación traduce los
/// índices únicos por nombre y la concurrencia, en Infrastructure (Application no referencia EF Core ni Npgsql).</summary>
public interface IShopifyUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Olvida lo cargado y lo pendiente. Tras perder una carrera (<c>concurrency.conflict</c>) la entidad en memoria tiene
    /// la versión vieja: sin esto, volver a leerla devolvería la misma instancia y el siguiente guardado chocaría otra vez.</summary>
    void DiscardPendingChanges();
}
