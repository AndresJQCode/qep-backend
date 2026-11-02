using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;

namespace Bootstrapper.ApiKeys;

/// <summary>
/// Vacía <see cref="ExternalApiCallLogChannel"/> de a una fila, cada una en su propio scope y sin transacción compartida con nadie. Una
/// escritura que falla se loguea y se absorbe: el servicio sigue con la siguiente, porque una fila mala no puede apagar el registro.
/// Al apagar drena lo que quedó, con un tope corto para no colgar el cierre del host.
/// </summary>
public sealed partial class ExternalApiCallLogWriter(
    ExternalApiCallLogChannel queue,
    IServiceScopeFactory scopes,
    ILogger<ExternalApiCallLogWriter> logger) : BackgroundService
{
    internal static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // CancellationToken.None al escribir: el del request ya no vale y una llamada cortada por el cliente también se registra.
            await foreach (var row in queue.Reader.ReadAllAsync(stoppingToken))
            {
                await WriteAsync(row, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagando: se cae al drenaje de abajo.
        }

        await DrainAsync();
    }

    private async Task DrainAsync()
    {
        using var deadline = new CancellationTokenSource(ShutdownDrainTimeout);
        while (!deadline.IsCancellationRequested && queue.Reader.TryRead(out var row))
        {
            await WriteAsync(row, deadline.Token);
        }
    }

    private async Task WriteAsync(ApiKeyRequest row, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IApiKeyRequestLog>().RecordAsync(row, cancellationToken);
        }
#pragma warning disable CA1031 // El registro nunca rompe nada: una fila que falla se loguea y el servicio sigue con la siguiente.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogRecordingFailed(logger, exception, row.Method, row.Path);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Could not record the external API call {Method} {Path}.")]
    private static partial void LogRecordingFailed(ILogger logger, Exception exception, string method, string path);
}
