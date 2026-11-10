using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Catalog.Application;
using Modules.Catalog.Infrastructure.Persistence;
using Modules.Customers.Infrastructure.Persistence;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Adapta Quotations al puerto <see cref="IProductDeletionCleanup"/> de Catalog (spec 2026-11-08). Vive en el
/// composition root por la misma razón que <see cref="CustomerMergeReferences"/> y <see cref="CatalogVariantUsageChecker"/>:
/// Catalog no referencia los otros módulos (<c>CatalogLayerTests</c>) y el acoplamiento entre esquemas se cablea acá.
///
/// **Corre sobre la conexión y la transacción del <see cref="CatalogDbContext"/> del scope.** El
/// <see cref="QuotationsDbContext"/> del mismo scope se enrola en ellas antes de llamar a
/// <see cref="IQuotationVariantDeletion"/>, así que el congelado de las líneas y los borradores revalorizados quedan
/// dentro del borrado del producto: si algo falla o nadie confirma, no queda nada. Con otra conexión la palabra
/// «transacción» no significaría nada. El enrolamiento dura lo que el scope: después de confirmar, ese
/// <see cref="QuotationsDbContext"/> sigue apuntando a una transacción terminada, así que nada en el mismo scope debe
/// volver a usarlo.
///
/// Un pedido de la tienda ya es un <c>Order</c> (spec 2026-11-09), así que sus líneas las cubre
/// <c>DeletedVariantLineStore</c> como las de cualquier otro pedido; Store no guarda líneas propias.
///
/// **El costo de este camino.** Conoce, a través de su servicio, las columnas de <c>quotations.quotation_items</c> y
/// <c>quotations.order_items</c> que toca <c>DeletedVariantLineStore</c>. Renombrar cualquiera compila y deja el
/// <c>DELETE</c> en 500 con <c>42703 undefined_column</c>. Lo amarra <c>ProductDeletionCleanupTests</c> (Quotations), que
/// resuelve este adaptador del contenedor y lo corre contra el esquema migrado.
/// </summary>
internal sealed class CatalogProductDeletionCleanup(
    CatalogDbContext catalog,
    QuotationsDbContext quotations,
    IQuotationVariantDeletion quotationLines) : IProductDeletionCleanup
{
    public async Task ApplyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> variantIds, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variantIds);
        var transaction = catalog.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Product deletion cleanup must run inside the catalog transaction.");
        if (variantIds.Count == 0)
        {
            return;
        }

        quotations.Database.SetDbConnection(catalog.Database.GetDbConnection(), contextOwnsConnection: false);
        await quotations.Database.UseTransactionAsync(transaction.GetDbTransaction(), cancellationToken);

        // Un choque con otra escritura es 412 concurrency.conflict, nunca 500 (CustomersWriteRaces, como en
        // CustomerMergeReferences): el borrador que otro guardó lo traduce el unit of work de Quotations; un deadlock o una
        // falla de serialización del SQL crudo de las líneas no pasa por ningún unit of work, así que se traduce en este punto.
        await CustomersWriteRaces.RunAsync(
            () => ApplyCoreAsync(tenantId, variantIds.Distinct().ToArray(), occurredAt, cancellationToken));
    }

    private Task ApplyCoreAsync(Guid tenantId, Guid[] ids, DateTimeOffset occurredAt, CancellationToken cancellationToken) =>
        quotationLines.ApplyAsync(tenantId, ids, occurredAt, cancellationToken);
}
