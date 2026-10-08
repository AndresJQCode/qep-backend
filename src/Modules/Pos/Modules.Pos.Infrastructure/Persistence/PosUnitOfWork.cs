using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Pos.Application;
using Modules.Pos.Domain;
using Npgsql;

namespace Modules.Pos.Infrastructure.Persistence;

/// <summary>
/// Traduce los errores de base por nombre de índice o constraint, no sólo por SqlState (spec,
/// «Traducción de errores»): hay varios únicos y devolver el código equivocado manda a corregir lo
/// equivocado.
/// </summary>
internal sealed class PosUnitOfWork(PosDbContext dbContext) : IPosUnitOfWork
{
    private const string OneOpenPerCashierIndex = "IX_cash_sessions_one_open_per_cashier";
    private const string SalesPrimaryKey = "PK_sales";

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
                "concurrency.conflict",
                "The cash session changed while the operation was being committed.",
                exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, OneOpenPerCashierIndex))
        {
            throw new PosDomainException(
                "pos.session.already_open", "The cashier already has an open cash session.");
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, SalesPrimaryKey))
        {
            // Interno: CreatePosSaleHandler lo consume y relee (paso 8). Nunca sale por HTTP.
            throw new PosDomainException("pos.sale.id_taken", "The sale id is already in use.");
        }
    }

    public async Task<IPosTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new PosTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is { } transaction)
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
        }

        dbContext.ChangeTracker.Clear();
    }

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);

    private sealed class PosTransaction(IDbContextTransaction transaction) : IPosTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
