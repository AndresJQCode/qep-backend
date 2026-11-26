using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Purchases.Application;
using Npgsql;

namespace Modules.Purchases.Infrastructure.Persistence;

internal sealed class PurchasesUnitOfWork(PurchasesDbContext dbContext) : IPurchasesUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Traducido acá y no en Application, que no referencia EF Core.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The purchase order changed while the update was being committed.",
                exception);
        }
        // Por nombre de índice y no por 23505 (precedente TenancyUnitOfWork). El contador serializa las altas, así que dos órdenes
        // con el mismo número son un bug del contador: un 500 que se ve, no un 422 que lo esconde.
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, PurchasesDbContext.OrderNumberIndex))
        {
            throw new InvalidOperationException(
                "Two purchase orders got the same number; the order number counter is broken.", exception);
        }
    }

    // Copia de SupportUnitOfWork.BeginTransactionAsync: el Dispose sin Commit deshace todo, también el número emitido.
    public async Task<IPurchasesTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new PurchasesTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);

    private sealed class PurchasesTransaction(IDbContextTransaction transaction) : IPurchasesTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
