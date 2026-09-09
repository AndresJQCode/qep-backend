using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Platform.Application;
using Modules.Platform.Domain;

namespace Modules.Platform.Infrastructure.Persistence;

/// <summary>Ver <see cref="IRequestFailureLog"/>: por qué usa un contexto propio y por qué no
/// propaga sus errores.</summary>
internal sealed partial class RequestFailureLog(
    DbContextOptions<PlatformDbContext> options,
    ILogger<RequestFailureLog> logger)
    : IRequestFailureLog
{
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "No se pudo guardar la falla de {Method} {Path} en el log de la aplicacion. " +
                  "La respuesta al cliente no se toca; lo que se pierde es el registro.")]
    private static partial void LogRecordFailed(
        ILogger logger, string method, string path, Exception exception);

    public async Task RecordAsync(RequestFailure failure, CancellationToken cancellationToken)
    {
        try
        {
            // Contexto propio: el del modulo que fallo puede tener cambios a medias que
            // justamente no deben commitearse.
            await using var dbContext = new PlatformDbContext(options);

            dbContext.RequestFailures.Add(failure);

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Deliberadamente ancho. Se ejecuta mientras se escribe la respuesta de error: una
        // excepcion desde aca convertiria una falla explicada en un 500 mudo.
        catch (Exception exception)
        {
            LogRecordFailed(logger, failure.Method, failure.Path, exception);
        }
    }
}
