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
    public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken) =>
        await dbContext.Database.BeginTransactionAsync(cancellationToken);

    // Spec 2026-11-12 D1, con la forma de AutomationsUnitOfWork: el candado se suelta con la transacción, así que sin una
    // abierta no serializaría nada.
    public async Task LockShopifyTokenAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        await dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({ShopifyTokenLock.Namespace}, hashtext({ownerId.ToString()}))", cancellationToken);
    }

    public async Task<bool> TryLockShopifyTokenAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var taken = await dbContext.Database.SqlQuery<bool>(
                $"SELECT pg_try_advisory_xact_lock({ShopifyTokenLock.Namespace}, hashtext({ownerId.ToString()})) AS \"Value\"")
            .ToListAsync(cancellationToken);
        return taken.Single();
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var saved = await dbContext.SaveChangesAsync(cancellationToken);
            // Con la transacción del candado abierta, guardar es confirmar (el handle sin confirmar deshace al disponerse).
            if (dbContext.Database.CurrentTransaction is { } transaction)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return saved;
        }
        // Primero: DbUpdateConcurrencyException hereda de DbUpdateException.
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The connection changed while the operation was being committed.",
                exception);
        }
        // La misma tienda enlazada a la vez por dos requests (spec 2026-10-15 §4.1). El índice de la ruta es el mismo para todos
        // los proveedores, así que se decide por la ruta de Shopify que se estaba agregando; el del nombre también entra,
        // porque en un doble clic las dos conexiones se llaman igual y ese choque puede salir primero. Quién ganó lo dice la
        // ruta que quedó: si es del mismo tenant (doble clic), el perdedor recibe concurrency.conflict —ya está enlazada,
        // recargar la muestra—; si es de otro, shop_already_linked. Nunca «otro tenant» a quien enlazó en el suyo.
        catch (DbUpdateException exception) when ((IsUniqueViolation(exception, IntegrationsDbContext.RouteExternalIndex)
                || IsUniqueViolation(exception, IntegrationsDbContext.ConnectionNameIndex))
            && AddedShopifyRoute() is not null)
        {
            var added = AddedShopifyRoute()!;
            var winner = await dbContext.Routes.AsNoTracking()
                .Where(route => route.ProviderKey == added.ProviderKey && route.ExternalId == added.ExternalId)
                .Select(route => (Guid?)route.TenantId)
                .SingleOrDefaultAsync(cancellationToken);
            if (winner == added.TenantId)
            {
                throw new RequestConcurrencyException(
                    "concurrency.conflict",
                    "The shop was linked to this tenant by another request meanwhile; reload to see the connection.",
                    exception);
            }

            if (winner is not null)
            {
                throw new IntegrationsDomainException(
                    IntegrationsErrorCodes.ShopifyShopAlreadyLinked,
                    "The shop is already linked to another tenant.",
                    exception);
            }

            // Sin ruta ganadora el choque fue sólo de nombre, con otra tienda: el name_taken de siempre.
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.NameTaken,
                "Another connection of this provider already uses that name.",
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

    private void EnsureTransaction()
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The Shopify token lock requires the transaction of BeginTransactionAsync.");
        }
    }

    private IntegrationConnectionRoute? AddedShopifyRoute() =>
        dbContext.ChangeTracker.Entries<IntegrationConnectionRoute>()
            .Where(entry => entry.State == EntityState.Added
                && string.Equals(entry.Entity.ProviderKey, IntegrationProviders.Shopify.Key, StringComparison.Ordinal))
            .Select(entry => entry.Entity)
            .FirstOrDefault();

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
}
