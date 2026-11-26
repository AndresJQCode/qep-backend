using Microsoft.Extensions.Logging;
using Modules.Authorization.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Tenancy → Authorization (spec 2026-11-25, roles por tenant): el tenant recién nacido, por signup o por invitación, recibe
/// los roles de las plantillas.
/// </summary>
/// <remarks>
/// Un fallo se registra como <c>Error</c> y no sube: el tenant ya quedó confirmado, su owner es <c>admin</c> y el siguiente
/// arranque siembra todo tenant sin marca. La única excepción que sube es la cancelación que pidió quien llama.
/// </remarks>
internal sealed partial class TenantRolesProvisioning(
    ITenantRoleSeeder seeder,
    ILogger<TenantRolesProvisioning> logger)
    : INewTenantProvisioning
{
    public async Task ProvisionAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await seeder.SeedAsync(tenantId.Value, cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogSeedFailed(logger, tenantId.Value, exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "The roles of the new tenant {TenantId} were not seeded. The tenant keeps only the system role until "
            + "the next startup seeds it.")]
    private static partial void LogSeedFailed(ILogger logger, Guid tenantId, Exception exception);
}
