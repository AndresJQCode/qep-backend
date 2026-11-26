using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Hr.Application;

namespace Bootstrapper.Valkey.LegalParameters;

/// <summary>
/// Sube la versión de la caché de valores legales una vez al arrancar, sólo con <c>Valkey:Areas:LegalParameters</c> prendido. Con el
/// área prendida la tabla vive 1 hora en Valkey y sobrevive a los reinicios; una migración que siembra el año, un hotfix de SQL o
/// una restauración no pasan por el escritor y no suben la versión. Con esto cada despliegue arranca con la tabla de la base. Es
/// inocuo con varias réplicas (cada una invalida una vez más) y <b>nunca lanza</b>: un Valkey caído no frena el arranque.
/// </summary>
internal sealed partial class LegalParameterCacheStartupBump(
    ILegalParameterCacheVersion version,
    ILogger<LegalParameterCacheStartupBump> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await version.BumpAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogBumpFailed(logger, exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "The legal parameters cache version could not be bumped at startup.")]
    private static partial void LogBumpFailed(ILogger logger, Exception exception);
}
