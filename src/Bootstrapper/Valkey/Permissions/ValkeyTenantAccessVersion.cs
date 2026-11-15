using Microsoft.Extensions.Logging;
using Modules.Tenancy.Application;
using StackExchange.Redis;

namespace Bootstrapper.Valkey.Permissions;

/// <summary>
/// La versión de acceso del tenant con <c>Valkey:Areas:Permissions</c> prendido (spec 2026-11-13, D-5): en un <c>MULTI</c>,
/// <c>SET NX</c> con la base de tiempo (<see cref="TenantAccessVersionBase"/>), <c>INCR</c> de <c>qep:tenantver:{tenantId}</c> y
/// <c>EXPIRE</c> de 30 días. La siembra hace que una clave desalojada no vuelva a contar desde 1 y alcance la versión de una entrada
/// sobreviviente (ABA). Sin éxito, log <c>Error</c> con el tenant (nunca el host ni la cadena): hasta que la entrada venza (60 s)
/// el cambio no se ve.
/// </summary>
/// <remarks>
/// No usa el token de quien llama: el segundo bump corre después del commit, y un request cancelado en ese instante dejaría la
/// caché sirviendo los permisos de antes. El tope lo pone <c>OperationTimeoutMs</c> del gateway.
/// </remarks>
internal sealed partial class ValkeyTenantAccessVersion(
    ValkeyGateway gateway,
    TimeProvider time,
    ILogger<ValkeyTenantAccessVersion> logger)
    : ITenantAccessVersion
{
    public async Task BumpAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var key = PermissionKeys.TenantVersion(tenantId);
        var seed = TenantAccessVersionBase.Seed(time);
        var attempt = await gateway.TryAsync(
            ValkeyArea.Permissions,
            async database =>
            {
                var transaction = database.CreateTransaction();
                var seeded = transaction.StringSetAsync(key, seed, TenantAccessVersionBase.Ttl, When.NotExists);
                var increment = transaction.StringIncrementAsync(key);
                var expire = transaction.KeyExpireAsync(key, TenantAccessVersionBase.Ttl);
                var committed = await transaction.ExecuteAsync().ConfigureAwait(false);
                await Task.WhenAll(seeded, increment, expire).ConfigureAwait(false);
                return committed;
            },
            CancellationToken.None).ConfigureAwait(false);
        if (attempt is not { Succeeded: true, Value: true })
        {
            LogBumpFailed(logger, tenantId);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Tenant access version for tenant {TenantId} could not be bumped; cached permissions of that tenant may be served for up to 60 seconds.")]
    private static partial void LogBumpFailed(ILogger logger, Guid tenantId);
}

/// <summary>La versión de acceso con el área apagada: no hay caché que invalidar.</summary>
internal sealed class NullTenantAccessVersion : ITenantAccessVersion
{
    public Task BumpAsync(Guid tenantId, CancellationToken cancellationToken) => Task.CompletedTask;
}
