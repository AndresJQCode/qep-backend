using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;
using Npgsql;

namespace Modules.Wholesale.Infrastructure.Persistence;

/// <summary>
/// Guarda y traduce las violaciones de la base a los códigos del contrato. Se discrimina por el <b>nombre</b> del índice o de la
/// FK y no sólo por el SqlState: 23505 dice que se violó algún índice único, no cuál. Traducido acá y no en Application, que no
/// referencia Npgsql (<c>WholesaleLayerTests</c>).
/// </summary>
internal sealed class WholesaleUnitOfWork(WholesaleDbContext dbContext) : IWholesaleUnitOfWork
{
    public void DiscardPendingChanges() => dbContext.ChangeTracker.Clear();

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (Translate(exception) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>La traducción sola, para probarla sin base. <c>null</c>: no es nuestra y sube tal cual.</summary>
    internal static Exception? Translate(Exception exception) => exception switch
    {
        // Antes que DbUpdateException, de la que hereda: al revés saldría como 500 en vez de 412.
        DbUpdateConcurrencyException => new RequestConcurrencyException(
            "concurrency.conflict", "The resource changed while the update was being committed.", exception),
        DbUpdateException update when IsViolation(
            update, PostgresErrorCodes.UniqueViolation, WholesaleDbContext.DistributorIdentificationIndex) =>
            new ResourceConflictException(
                WholesaleErrorCodes.DistributorIdentificationTaken,
                "Another distributor already has that identification number.",
                exception),
        // Dos aceptaciones vinculando el mismo tenant a la vez: el servicio de invitación relee una vez.
        DbUpdateException update when IsViolation(update, PostgresErrorCodes.UniqueViolation, WholesaleDbContext.DistributorTenantIndex) =>
            new ResourceConflictException(
                WholesaleErrorCodes.DistributorAlreadyLinked,
                "That organization is already a distributor of this wholesaler.",
                exception),
        _ => null,
    };

    private static bool IsViolation(DbUpdateException exception, string sqlState, string constraintName) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == sqlState
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
}
