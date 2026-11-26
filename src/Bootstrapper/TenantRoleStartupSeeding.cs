using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Authorization.Application;

namespace Bootstrapper;

/// <summary>
/// La siembra de roles del arranque (spec 2026-11-25, roles por tenant): repara todo tenant sin la marca, sea anterior a la
/// versión que trajo las plantillas, sembrado por <c>QepSeedRunner</c> o uno cuyo alta no pudo sembrar.
/// </summary>
public static partial class TenantRoleStartupSeeding
{
    /// <summary>
    /// Corre bajo el candado de arranque y después de la semilla. Una falla sube y detiene el arranque: atender sin los roles
    /// dejaría miembros sin permisos y sin ningún error.
    /// </summary>
    public static async Task SeedTenantRolesAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var seeded = await scope.ServiceProvider.GetRequiredService<ITenantRoleSeeder>()
            .SeedMissingAsync(cancellationToken);
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(TenantRoleStartupSeeding).FullName!);
        LogSeeded(logger, seeded);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant roles seeded at startup for {TenantCount} tenants.")]
    private static partial void LogSeeded(ILogger logger, int tenantCount);
}
