using Microsoft.Extensions.Logging;
using Modules.Quotations.Application;

namespace Modules.Quotations.Infrastructure.Whatsapp;

// Envío de desarrollo: registra el mensaje en vez de mandarlo, mismo criterio que
// `LogEmailChannel` en Notifications. Es el default mientras `Quotations:WhatsApp:*` no esté
// configurado — así "Enviar" sigue funcionando en cualquier ambiente sin credenciales de
// Zenvia (todas las pruebas de integración incluidas) en vez de bloquearse por una integración
// que todavía no se aprovisionó.
internal sealed partial class LogWhatsAppSender(ILogger<LogWhatsAppSender> logger)
    : IWhatsAppSender
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "WhatsApp (dev sender) quotation {OrderNumber} to {ToPhone}")]
    private static partial void LogMessage(ILogger logger, string orderNumber, string? toPhone);

    public Task SendQuotationAsync(
        WhatsAppQuotationMessage message, CancellationToken cancellationToken)
    {
        LogMessage(logger, message.OrderNumber, message.ToPhone);
        return Task.CompletedTask;
    }
}
