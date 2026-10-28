namespace Modules.Catalog.Application;

/// <summary>
/// ¿Alguna línea vendida guarda esta variante? (spec 2026-10-20 §3.3). «En uso» es tener al menos
/// una fila en <c>quotations.quotation_items</c>, <c>quotations.order_items</c> o
/// <c>pos.sale_lines</c> con su <c>variant_id</c>. Una variante en uso no se borra: se desactiva,
/// para que ninguna línea quede con un <c>variant_id</c> huérfano.
///
/// **Es un puerto de Catalog, de sólo lectura.** Catalog no referencia Quotations ni Pos
/// (<c>CatalogLayerTests</c>), así que la consulta entre esquemas vive en el composition root
/// (<c>Bootstrapper.CatalogVariantUsageChecker</c>), igual que <c>PriceChangeReportSource</c>.
///
/// El adaptador corre sobre la conexión del <c>CatalogDbContext</c> de la petición: dentro de una
/// transacción abierta con <see cref="ICatalogUnitOfWork.BeginTransactionAsync"/>, la consulta es
/// parte de esa transacción. Es lo que usa el PUT para repetir la comprobación justo antes de
/// borrar (Enfoque de revisión 3).
///
/// Las opciones y los valores de la biblioteca **no** pasan por acá: su uso es por variantes, que
/// viven en Catalog, y lo resuelve <c>IOptionDefinitionRepository</c> sin puerto (spec §3.1).
/// </summary>
public interface IVariantUsageChecker
{
    /// <summary>
    /// Los ids de <paramref name="variantIds"/> que tienen al menos una línea en el tenant. Los que
    /// no aparecen no están en uso. Con una colección vacía devuelve vacío sin consultar.
    /// </summary>
    Task<IReadOnlySet<Guid>> FindUsedAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken);
}
