using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>
/// Ver <see cref="IQuotationSendFailureLog"/> para por qué esto no pasa por
/// <c>IQuotationsUnitOfWork</c>.
/// </summary>
internal sealed partial class QuotationSendFailureLog(
    DbContextOptions<QuotationsDbContext> options,
    ILogger<QuotationSendFailureLog> logger)
    : IQuotationSendFailureLog
{
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "No se pudo anotar en el historial la falla de envio de la cotizacion " +
                  "{QuotationId}. El envio fallo igual; lo que se pierde es la anotacion.")]
    private static partial void LogRecordFailed(
        ILogger logger, Guid quotationId, Exception exception);

    public async Task RecordAsync(
        QuotationHistoryEntry historyEntry,
        CancellationToken cancellationToken)
    {
        try
        {
            // Contexto propio y no el del request: ese quedó con los cambios a medias del envío
            // que falló, y guardarlo commitearía justo lo que no debe commitearse.
            await using var dbContext = new QuotationsDbContext(options);

            dbContext.QuotationHistoryEntries.Add(historyEntry);

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Deliberadamente ancho. Es el último recurso de un camino de error: cualquier excepción
        // que salga de acá reemplazaría a la que el llamador está por relanzar, que es la que de
        // verdad explica qué pasó. Ver IQuotationSendFailureLog.
        catch (Exception exception)
        {
            LogRecordFailed(logger, historyEntry.QuotationId.Value, exception);
        }
    }
}
