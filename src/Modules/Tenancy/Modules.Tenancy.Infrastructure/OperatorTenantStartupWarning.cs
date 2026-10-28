using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modules.Tenancy.Infrastructure;

/// <summary>
/// Spec 2026-10-08 D10: en producción, arrancar sin <c>Platform:OperatorTenantId</c> no falla, pero
/// deja una advertencia. Desde que <c>appsettings.json</c> trae por defecto el id fijo del tenant
/// sembrado (<c>TenancySeeder.OperatorTenantId</c>), esto sólo pasa si alguien vació la clave a
/// propósito para apagar la consola. Que el id exista como tenant no se mira acá: si no existe, nadie
/// tiene membresía en él y la consola no es alcanzable.
///
/// Es un servicio hospedado y no parte del validador para que el validador no dependa del host: las
/// pruebas que arman <c>AddTenancyInfrastructure</c> sin host siguen pudiendo validar. Pública sólo
/// para probarla sin <c>InternalsVisibleTo</c>, igual que <see cref="TenantModuleDefaults"/>.
/// </summary>
public sealed class OperatorTenantStartupWarning(
    IOptions<OperatorTenantOptions> options,
    IHostEnvironment environment,
    ILogger<OperatorTenantStartupWarning> logger) : IHostedService
{
    private static readonly Action<ILogger, Exception?> LogMissingOperatorTenant =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(4101, nameof(LogMissingOperatorTenant)),
            "Platform:OperatorTenantId is not configured: the operator console is disabled and operator.* permissions are dropped in every tenant.");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (environment.IsProduction() && options.Value.OperatorTenantId is null)
        {
            LogMissingOperatorTenant(logger, null);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
