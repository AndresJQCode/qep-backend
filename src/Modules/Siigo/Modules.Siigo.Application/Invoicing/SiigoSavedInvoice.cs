using Microsoft.Extensions.Logging;
using Modules.Siigo.Domain;

namespace Modules.Siigo.Application.Invoicing;

/// <summary>La respuesta de sellar y de resolver: la fila <b>ya guardada</b>, con lo que se lee después del commit. Nada de eso puede
/// hacer fallar la petición: la operación ya está hecha (hasta un sellado ante la DIAN) y repetirla respondería 422. La anulación arma
/// sus filas aparte y comparte <see cref="IsQueuedAsync"/>.</summary>
internal static partial class SiigoSavedInvoice
{
    public static async Task<SiigoInvoiceDto> ToDtoAsync(
        ISiigoInvoiceBatchRepository batches,
        ISiigoOrderSource orders,
        SiigoInvoice row,
        DateTimeOffset now,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var queued = await IsQueuedAsync(batches, row, logger, cancellationToken);
        var orderMarkFailed = await SiigoOrderMark.IsMissingAsync(orders, row, queued, logger, cancellationToken);
        return SiigoInvoiceDto.From(row, now, orderMarkFailed, queued: queued);
    }

    // Sin respuesta se asume lo conservador, que alguna tanda tiene el pedido: no se ofrece reenviar ni se avisa de la marca, y la próxima
    // lectura del pedido lo dice bien. La cancelación de quien llama sí sube, como en el resto de las lecturas: ya no hay a quién
    // responder. El log lleva sólo el tipo de la excepción.
    internal static async Task<bool> IsQueuedAsync(
        ISiigoInvoiceBatchRepository batches, SiigoInvoice row, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            return await batches.FindOpenBatchIdAsync(row.TenantId, row.OrderId, cancellationToken) is not null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogQueueReadFailed(logger, exception.GetType().Name);
            return true;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Reading whether a batch holds the order failed ({ExceptionType}) after the invoice was saved; the answer treats it as queued.")]
    private static partial void LogQueueReadFailed(ILogger logger, string exceptionType);
}
