using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;

namespace Modules.ApiAccess.Infrastructure.Persistence;

/// <summary>Ver <see cref="IApiKeyRequestLog"/>: por qué usa un contexto propio y por qué no
/// propaga sus errores.</summary>
internal sealed partial class ApiKeyRequestLog(
    DbContextOptions<ApiAccessDbContext> options,
    ILogger<ApiKeyRequestLog> logger)
    : IApiKeyRequestLog
{
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "No se pudo guardar la llamada {Method} {Path} en el registro de la API externa. " +
                  "La respuesta al cliente no se toca; lo que se pierde es el registro.")]
    private static partial void LogRecordFailed(
        ILogger logger, string method, string path, Exception exception);

    public async Task RecordAsync(ApiKeyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // Contexto propio: sin transacción compartida con el request.
            await using var dbContext = new ApiAccessDbContext(options);

            dbContext.ApiKeyRequests.Add(request);

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Deliberadamente ancho: se escribe después de responder y un fallo aquí no puede subir.
        catch (Exception exception)
        {
            LogRecordFailed(logger, request.Method, request.Path, exception);
        }
    }
}
