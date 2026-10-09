using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Npgsql;

namespace Modules.Integrations.Infrastructure.Persistence;

/// <summary>
/// Traduce los errores de base por nombre de índice, no sólo por SqlState (CLAUDE.md): el único del
/// nombre es <c>name_taken</c>; el de la ruta, <c>number_already_connected</c>; la versión vieja es
/// <c>concurrency.conflict</c>.
/// </summary>
internal sealed class IntegrationsUnitOfWork(IntegrationsDbContext dbContext) : IIntegrationsUnitOfWork
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
                "concurrency.conflict",
                "The connection changed while the operation was being committed.",
                exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, IntegrationsDbContext.RouteExternalIndex))
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.WhatsAppNumberAlreadyConnected,
                "That WhatsApp number is already connected to another connection.",
                exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, IntegrationsDbContext.ConnectionNameIndex))
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.NameTaken,
                "Another connection of this provider already uses that name.",
                exception);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
}
