using Microsoft.Extensions.Logging;

namespace Modules.Siigo.Application.Invoicing;

/// <summary>
/// Anotar en la bandeja del operador la ciudad del exterior que no se pudo vincular (spec 2026-11-26). Lo llaman sólo la vista previa y
/// la emisión; la disponibilidad no. La bandeja es de Geography y usa su propio <c>DbContext</c>: la anotación no entra en la transacción
/// de Siigo ni la tumba. Una falla deja un warning y la facturación sigue (el pedido queda <c>not_ready</c> igual, por
/// <c>customer_city_unlinked</c>).
/// </summary>
internal static partial class SiigoForeignCityAnnotation
{
    // La cancelación de quien llama sí sube: ya no hay a quién responder. El log lleva sólo el tipo de la excepción, nunca el texto de la
    // ciudad, que es del cliente.
    public static async Task AnnotateAsync(
        ISiigoForeignCityInbox inbox,
        ILogger logger,
        Guid tenantId,
        SiigoInvoiceInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(input);
        if (input.BilledParty.UnlinkedCity is not { Reportable: true } city)
        {
            return;
        }

        try
        {
            await inbox.ReportAsync(tenantId, input.OrderId, city, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogAnnotationFailed(logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Annotating the foreign city of the order in the operator inbox failed ({ExceptionType}); invoicing goes on.")]
    private static partial void LogAnnotationFailed(ILogger logger, string exceptionType);
}
