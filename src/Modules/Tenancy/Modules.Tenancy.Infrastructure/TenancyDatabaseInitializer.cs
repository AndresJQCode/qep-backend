using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.Infrastructure;

/// <summary>
/// Aplica las migraciones de Tenancy al arrancar. **No siembra nada.**
///
/// Hasta el 2026-09-21 creaba además un tenant <c>qcode-demo</c> en Development. Ese tenant no
/// servía para nada: se creaba sin ninguna membresía, y era el único tenant del sistema que
/// quedaba sin owner, que es justo lo que la guarda del agregado necesita para valer
/// (<c>Tenant.OwnerMembershipId</c>). En ese momento nadie lo referenciaba desde código de
/// producción, pero varias pruebas de integración de Tenancy y Notifications sí tenían
/// hardcodeado su id (<c>MembershipApiTests</c>, <c>TenantSettingsApiTests</c>,
/// <c>TenantLogoApiTests</c>, <c>InvitationApiTests</c>, <c>AuthSessionApiTests</c>,
/// <c>TenantClockTests</c>, <c>InvitationNotificationTests</c>) y dependían de que este
/// inicializador lo sembrara. Quitarlo de acá no las rompió: cada una siembra ahora su propio
/// tenant con ese mismo id, directo en su base de Testcontainers, sin volver a depender de este
/// archivo.
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
