using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Catalog.Application;
using Modules.Catalog.Infrastructure.Persistence;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;

namespace Bootstrapper;

/// <summary>
/// Adapta Quotations y Store al puerto <see cref="IProductDeletionCleanup"/> de Catalog (spec 2026-11-08). Vive en el
/// composition root por la misma razón que <see cref="CustomerMergeReferences"/> y <see cref="CatalogVariantUsageChecker"/>:
/// Catalog no referencia los otros módulos (<c>CatalogLayerTests</c>) y el acoplamiento entre esquemas se cablea acá.
///
/// **Corre sobre la conexión y la transacción del <see cref="CatalogDbContext"/> del scope.** El
/// <see cref="QuotationsDbContext"/> del mismo scope se enrola en ellas antes de llamar a
/// <see cref="IQuotationVariantDeletion"/>, así que el congelado de las líneas, los borradores revalorizados y el
/// <c>DELETE</c> de la tienda quedan dentro del borrado del producto: si algo falla o nadie confirma, no queda nada.
/// Con otra conexión la palabra «transacción» no significaría nada. El enrolamiento dura lo que el scope: después de
/// confirmar, ese <see cref="QuotationsDbContext"/> sigue apuntando a una transacción terminada, así que nada en el
/// mismo scope debe volver a usarlo (el <c>DELETE</c> no lo hace).
///
/// **El costo de este camino.** Conoce por nombre <c>store.order_requests</c> y <c>store.order_request_lines</c>
/// (<c>request_id</c>, <c>variant_id</c>, <c>status</c>, <c>version</c>) y, a través de su servicio, las columnas de
/// <c>quotations.quotation_items</c> y <c>quotations.order_items</c> que toca <c>DeletedVariantLineStore</c>. Renombrar
/// cualquiera compila y deja el <c>DELETE</c> en 500 con <c>42703 undefined_column</c> o <c>42P01 undefined_table</c>. Lo
/// amarran <c>ProductDeletionCleanupTests</c> (Quotations) y <c>ProductDeletionStoreCleanupTests</c> (Store), que
/// resuelven este adaptador del contenedor y lo corren contra el esquema migrado.
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

        // Un choque con otra escritura es 412 concurrency.conflict, nunca 500 (mismo criterio que CustomersWriteRaces): el
        // borrador que otro guardó lo traduce el unit of work de Quotations; un deadlock o una falla de serialización del
        // SQL de acá no pasa por ningún unit of work, así que se traduce en este punto.
        try
        {
            await ApplyCoreAsync(tenantId, variantIds.Distinct().ToArray(), occurredAt, cancellationToken);
        }
        catch (Exception exception) when (IsRace(exception))
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "Another write raced with this one. Reload and try again.", exception);
        }
    }

    private async Task ApplyCoreAsync(Guid tenantId, Guid[] ids, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        await quotationLines.ApplyAsync(tenantId, ids, occurredAt, cancellationToken);

        // Sólo las solicitudes nuevas: una tomada ya es un pedido y la descartada no vuelve a la bandeja. Los totales no
        // se tocan, son los que vio el comprador; al tomarla, el pedido sale de las líneas que quedan y la diferencia se
        // muestra. La versión sube para que la bandeja abierta en otra pestaña relea.
        await catalog.Database.ExecuteSqlAsync($"""
            WITH removed AS (
                DELETE FROM store.order_request_lines l
                USING store.order_requests r
                WHERE r.id = l.request_id AND r.tenant_id = {tenantId} AND r.status = 'New'
                  AND l.variant_id = ANY({ids})
                RETURNING l.request_id)
            UPDATE store.order_requests r SET version = r.version + 1
            WHERE r.id IN (SELECT request_id FROM removed);
            """, cancellationToken);
    }

    // Deadlock (40P01) o falla de serialización (40001), en toda la cadena de InnerException: SaveChanges los envuelve en
    // un DbUpdateException y la estrategia de ejecución de Npgsql en un InvalidOperationException.
    private static bool IsRace(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure })
            {
                return true;
            }
        }

        return false;
    }
}
