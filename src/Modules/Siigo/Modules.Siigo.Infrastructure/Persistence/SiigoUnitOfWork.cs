using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Siigo.Application;

namespace Modules.Siigo.Infrastructure.Persistence;

/// <summary>
/// Guarda y traduce las violaciones de la base a los códigos del contrato. Se discrimina por el <b>nombre</b> del índice y no sólo por
/// el SqlState: 23505 dice que se violó algún índice único, no cuál. Traducido acá y no en Application, que no referencia Npgsql
/// (<c>SiigoLayerTests</c>). Los índices únicos llegan con las tablas (migración <c>InitialSiigo</c>).
/// </summary>
internal sealed class SiigoUnitOfWork(SiigoDbContext dbContext) : ISiigoUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (Translate(exception) is { } translated)
        {
            throw translated;
        }
    }

    public async Task<ISiigoTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new SiigoTransaction(await dbContext.Database.BeginTransactionAsync(cancellationToken));

    public Task LockOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        // Fuera de una transacción el candado se soltaría al terminar la sentencia y no serializaría nada.
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("LockOrderAsync requires an open transaction (BeginTransactionAsync).");
        }

        var key = SiigoInvoiceLock.Key(orderId);
        return dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({SiigoInvoiceLock.Namespace}, hashtext({key}))", cancellationToken);
    }

    /// <summary>La traducción sola, para probarla sin base. <c>null</c>: no es nuestra y sube tal cual.</summary>
    internal static Exception? Translate(Exception exception) => exception switch
    {
        DbUpdateConcurrencyException => new RequestConcurrencyException(
            "concurrency.conflict", "The resource changed while the update was being committed.", exception),
        _ => null,
    };

    private sealed class SiigoTransaction(IDbContextTransaction transaction) : ISiigoTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        // Disponer sin commit hace rollback, y el rollback es lo que suelta los candados.
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
