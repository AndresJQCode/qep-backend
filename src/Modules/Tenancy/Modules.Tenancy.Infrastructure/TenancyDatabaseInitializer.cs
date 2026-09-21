using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.Infrastructure;

/// <summary>
/// Aplica las migraciones de Tenancy al arrancar. **No siembra nada.**
///
/// Hasta el 2026-09-21 creaba además un tenant <c>qcode-demo</c> en Development. Ese tenant no
/// servía para nada: se creaba sin ninguna membresía, y como los permisos se resuelven desde la
/// membresía, cualquier request contra él devolvía 403. Nadie lo referenciaba —ni un seeder, ni
/// una prueba, ni una fixture— y era el único tenant del sistema que quedaba sin owner, que es
/// justo lo que la guarda del agregado necesita para valer
/// (<c>Tenant.OwnerMembershipId</c>).
///
/// Un tenant creado desde acá **no puede** tener owner: acá no hay usuario a quien nombrar.
/// Sembrar un tenant usable es trabajo de <c>QepSeedRunner</c>, que crea el usuario, el tenant y
/// su membresía dueña juntos.
/// </summary>
public static class TenancyDatabaseInitializer
{
    public static async Task InitializeTenancyDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
