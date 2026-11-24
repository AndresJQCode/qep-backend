using Bootstrapper.Valkey.Permissions;
using Microsoft.Extensions.Logging;
using Modules.Hr.Application;
using StackExchange.Redis;

namespace Bootstrapper.Valkey.LegalParameters;

/// <summary>
/// La versión de la caché de valores legales con <c>Valkey:Areas:LegalParameters</c> prendido (spec 2026-11-22, «Caché»): en un
/// <c>MULTI</c>, <c>SET NX</c> con la base de tiempo, <c>INCR</c> de <c>qep:legalparams:ver</c> y <c>EXPIRE</c> de 30 días (la siembra
/// evita que una clave desalojada vuelva a contar desde 1 y alcance la versión de una tabla sobreviviente). Sin éxito el gateway
/// cuenta el respaldo del área y acá se loguea en <c>Error</c>: hasta que la tabla venza (1 hora) las réplicas pueden servir la
/// versión vieja. <b>Nunca lanza</b>: la escritura ya se confirmó.
/// </summary>
/// <remarks>
/// No usa el token de quien llama: corre después del commit, y un request cancelado en ese instante dejaría la caché sirviendo la
/// tabla de antes. El tope lo pone <c>OperationTimeoutMs</c> del gateway.
/// </remarks>
internal sealed partial class ValkeyLegalParameterCacheVersion(
    ValkeyGateway gateway,
    TimeProvider time,
    ILogger<ValkeyLegalParameterCacheVersion> logger)
    : ILegalParameterCacheVersion
{
    public async Task BumpAsync(CancellationToken cancellationToken)
    {
        var key = ValkeyKeys.LegalParametersVersion;
        var seed = TenantAccessVersionBase.Seed(time);
        var attempt = await gateway.TryAsync(
            ValkeyArea.LegalParameters,
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
            LogBumpFailed(logger);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "The legal parameters cache version could not be bumped; cached legal parameters may be served for up to 1 hour.")]
    private static partial void LogBumpFailed(ILogger logger);
}

/// <summary>La versión con el área apagada: sube el contador de la copia del pod que escribió.</summary>
internal sealed class MemoryLegalParameterCacheVersion(MemoryLegalParameterCache cache) : ILegalParameterCacheVersion
{
    public Task BumpAsync(CancellationToken cancellationToken)
    {
        cache.Bump();
        return Task.CompletedTask;
    }
}
