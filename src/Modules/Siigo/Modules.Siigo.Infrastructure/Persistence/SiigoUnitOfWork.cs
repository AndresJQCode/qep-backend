using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modules.Siigo.Application;
using Modules.Siigo.Domain;
using Npgsql;

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
        // Primero: DbUpdateConcurrencyException hereda de DbUpdateException.
        DbUpdateConcurrencyException => new RequestConcurrencyException(
            "concurrency.conflict", "The resource changed while the update was being committed.", exception),
        // §5.3: otra conexión del tenant ya factura con esa empresa. Es un 409 al asesor, no una carrera que se reintente.
        DbUpdateException update when IsUniqueViolation(update, SiigoDbContext.SettingsTenantCompanyIndex) => new ResourceConflictException(
            SiigoErrorCodes.SettingsCompanyTaken, "Another Siigo connection of this tenant already invoices with that company.", exception),
        // §5.6: ya hay una factura vigente de ese tipo para el pedido; el candado da la respuesta amable y el índice cierra la carrera.
        DbUpdateException update when IsUniqueViolation(update, SiigoDbContext.InvoicesTenantOrderKindIndex) => new ResourceConflictException(
            SiigoErrorCodes.InvoiceAlreadyExists, "The order already has a current Siigo invoice of that kind.", exception),
        // Spec 2026-11-24 §1: otra tanda tomó el pedido entre la consulta del handler y este guardado.
        DbUpdateException update when IsUniqueViolation(update, SiigoDbContext.InvoiceBatchItemsTenantOrderOpenIndex) =>
            new ResourceConflictException(
                SiigoErrorCodes.InvoiceBatchOrderInProgress, "An order is already in an unfinished invoice batch.", exception),
        _ => null,
    };

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);

    private sealed class SiigoTransaction(IDbContextTransaction transaction) : ISiigoTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        // Disponer sin commit hace rollback, y el rollback es lo que suelta los candados.
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
