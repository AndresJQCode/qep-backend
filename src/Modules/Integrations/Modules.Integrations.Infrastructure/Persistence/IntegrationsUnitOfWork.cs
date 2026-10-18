using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Npgsql;

namespace Modules.Integrations.Infrastructure.Persistence;

/// <summary>
/// Traduce los errores de base por nombre de índice, no sólo por SqlState (CLAUDE.md): el único del
/// nombre es <c>name_taken</c>; el de la ruta, <c>number_already_connected</c> (o <c>shop_already_linked</c> si la ruta
/// que se agregaba es de Shopify); la versión vieja es
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
        // La misma tienda enlazada a la vez desde dos tenants (spec 2026-10-15 §4.1): gana uno, el otro recibe el código de
        // Shopify. Se decide por la ruta que se estaba agregando, porque el índice es el mismo para todos los proveedores.
        catch (DbUpdateException exception) when (IsUniqueViolation(exception, IntegrationsDbContext.RouteExternalIndex)
            && AddsRouteOf(IntegrationProviders.Shopify.Key))
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.ShopifyShopAlreadyLinked,
                "The shop is already linked to another tenant.",
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

    private bool AddsRouteOf(string providerKey) =>
        dbContext.ChangeTracker.Entries<IntegrationConnectionRoute>()
            .Any(entry => entry.State == EntityState.Added && string.Equals(entry.Entity.ProviderKey, providerKey, StringComparison.Ordinal));

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
}
