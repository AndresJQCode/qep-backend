using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;
using Modules.Purchases.Application;

namespace Bootstrapper;

/// <summary>
/// Compras: la orden al proveedor por correo, sobre <see cref="IEmailChannel"/> y <see cref="PurchaseOrderEmailTemplate"/> de
/// Notifications, que Compras no ve. Calco de <see cref="SupportOpeningEmailSender"/>: cualquier excepción del canal es
/// <see cref="PurchaseOrderEmailResult.Failed"/> y la orden ya quedó enviada. Nunca loguea la dirección ni el contenido: sólo el tipo
/// de la excepción.
/// </summary>
internal sealed partial class PurchaseOrderEmailSender(IEmailChannel channel, ILogger<PurchaseOrderEmailSender> logger)
    : IPurchaseOrderEmail
{
    // Sólo el tipo de la excepción: el mensaje del proveedor puede traer la dirección del destinatario.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Purchase order email failed with {ExceptionType}; it is reported as failed.")]
    private static partial void LogSendFailed(ILogger logger, string exceptionType);

    public async Task<PurchaseOrderEmailResult> SendAsync(PurchaseOrderEmailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var document = request.Document;
        try
        {
            await channel.SendAsync(
                PurchaseOrderEmailTemplate.Render(new PurchaseOrderEmailInput(
                    request.ToAddress,
                    document.TenantName,
                    document.Number,
                    document.IssuedOn,
                    document.SupplierName,
                    document.Currency,
                    document.Notes,
                    document.Lines
                        .Select(line => new PurchaseOrderEmailLine(line.Code, line.Name, line.Quantity, line.UnitCost, line.Subtotal))
                        .ToList(),
                    document.Subtotal,
                    document.Tax,
                    document.Total,
                    request.FileName,
                    request.Pdf,
                    document.ShowsAmounts)),
                cancellationToken);
            return PurchaseOrderEmailResult.Sent;
        }
        // El timeout del HttpClient de Infobip (30 s) llega como TaskCanceledException: es un fallo del envío, no una cancelación
        // del cliente. Sólo ésta se propaga.
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogSendFailed(logger, exception.GetType().Name);
            return PurchaseOrderEmailResult.Failed;
        }
    }
}
