using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Shopify.Application;
using Npgsql;

namespace Modules.Shopify.Infrastructure.Persistence;

/// <summary>
/// Traduce los errores de base por nombre de índice, no sólo por SqlState (CLAUDE.md). El mismo pedido escrito a la vez por el
/// webhook y la conciliación choca con <c>IX_shopify_orders_connection_order</c>, y la tienda creada dos veces en su primer
/// contacto choca con <c>PK_stores</c>: en los dos casos ganó otra escritura, así que el perdedor recibe
/// <c>concurrency.conflict</c> y vuelve a leer la fila del ganador. La versión vieja de la tienda es el mismo código. Traducido acá
/// y no en Application, que no referencia Npgsql (ShopifyLayerTests).
/// </summary>
internal sealed class ShopifyUnitOfWork(ShopifyDbContext dbContext) : IShopifyUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Primero: DbUpdateConcurrencyException hereda de DbUpdateException.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The Shopify store changed while the operation was being committed.", exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, ShopifyDbContext.OrderConnectionIndex))
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The Shopify order was written by another process meanwhile; reload it.", exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, ShopifyDbContext.StorePrimaryKey))
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The Shopify store was created by another process meanwhile; reload it.", exception);
        }
    }

    public void DiscardPendingChanges() => dbContext.ChangeTracker.Clear();

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
}
