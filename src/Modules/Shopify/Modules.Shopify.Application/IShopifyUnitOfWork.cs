namespace Modules.Shopify.Application;

/// <summary>Guarda las tiendas, el espejo, las entregas y su auditoría en una sola transacción. La implementación traduce los
/// índices únicos por nombre y la concurrencia, en Infrastructure (Application no referencia EF Core ni Npgsql).</summary>
public interface IShopifyUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
