using Microsoft.Extensions.Logging;
using Modules.Notifications.Application;

namespace Modules.Notifications.Infrastructure.Channels;

// Canal de email de desarrollo: registra el mensaje en vez de enviarlo, para ejercitar todo
// el flujo de notificación sin un proveedor externo (ADR 0018).
internal sealed partial class LogEmailChannel(ILogger<LogEmailChannel> logger) : IEmailChannel
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Email (dev channel) to {ToAddress}: {Subject}")]
    private static partial void LogEmail(ILogger logger, string toAddress, string subject);

    // Soporte fase 4b: el nombre y el tamaño de cada adjunto, nunca su contenido.
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Email (dev channel) attachment {FileName} ({SizeBytes} bytes)")]
    private static partial void LogAttachment(ILogger logger, string fileName, int sizeBytes);

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        LogEmail(logger, message.ToAddress, message.Subject);
        foreach (var attachment in message.Attachments ?? [])
        {
            LogAttachment(logger, attachment.FileName, attachment.Content.Length);
        }

        return Task.CompletedTask;
    }
}
