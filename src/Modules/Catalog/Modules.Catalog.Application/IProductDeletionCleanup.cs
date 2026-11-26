namespace Modules.Catalog.Application;

/// <summary>
/// Lo que los otros módulos tienen que hacer cuando el catálogo borra un producto (spec 2026-11-08): congelar código y
/// nombre en las líneas de cotizaciones y pedidos que se quedan, sacar las líneas de los borradores y de las solicitudes
/// nuevas de la tienda. Catalog no referencia Quotations ni Store (<c>CatalogLayerTests</c>), así que el adaptador vive en
/// el composition root (<c>Bootstrapper.CatalogProductDeletionCleanup</c>).
/// </summary>
public interface IProductDeletionCleanup
{
    /// <summary>
    /// Corre dentro de una transacción abierta con <see cref="ICatalogUnitOfWork.BeginTransactionAsync"/> y
    /// **antes** de quitar el producto, porque necesita el código y el nombre de sus variantes. No confirma nada: lo
    /// que escribe queda en esa transacción. Sin transacción abierta lanza <see cref="InvalidOperationException"/>.
    /// </summary>
    Task ApplyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> variantIds,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);
}
