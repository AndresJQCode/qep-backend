using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
        // Spec 2026-10-26: la importación y un reintento escribieron a la vez la huella del mismo sinónimo y destino. Con el
        // mismo código, ApplyAsync descarta, relee y ve la del ganador.
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, ShopifyDbContext.CityCorrectionSynonymTargetIndex))
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The city correction was written by another process meanwhile; reload it.", exception);
        }
    }

    public void DiscardPendingChanges() => dbContext.ChangeTracker.Clear();

    public async Task<IShopifyTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new ShopifyTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    public Task LockCitySynonymAsync(Guid synonymId, CancellationToken cancellationToken)
    {
        // Fuera de una transacción el candado se soltaría al terminar la sentencia y no serializaría nada.
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("LockCitySynonymAsync requires an open transaction (BeginTransactionAsync).");
        }

        // El mismo texto que 'shopify.city-synonym:' || synonym_id::text en SQL (un Guid se formatea como el uuid de Postgres): un
        // candado tomado desde SQL crudo con esa expresión es el mismo candado.
        var key = $"shopify.city-synonym:{synonymId}";
        return dbContext.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    }

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);

    private sealed class ShopifyTransaction(IDbContextTransaction transaction) : IShopifyTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        // Disponer sin commit hace rollback, y el rollback es lo que suelta los candados.
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
