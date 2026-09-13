using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Identity.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume `quotations.export-ready.v1` y le manda a quien pidió la exportación el correo con el
// enlace. Calcado de CustomerExportDeliveryWorker: idempotente por el inbox propio, un commit por
// mensaje para que una falla no bloquee el lote, y el enlace ya viene prefirmado —este módulo no
// conoce Storage—. Un worker por evento, igual que clientes y productos.
internal sealed partial class QuotationsExportReadyDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<QuotationsExportReadyDeliveryWorker> logger) : BackgroundService
{
    private const string Consumer = "notifications.quotations-export-ready-email";
    private const string EventName = "quotations.export-ready.v1";
    private const int BatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [LoggerMessage(Level = LogLevel.Error, Message = "Quotations export ready delivery tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var channel = scope.ServiceProvider.GetRequiredService<IEmailChannel>();
        var userDirectory = scope.ServiceProvider.GetRequiredService<IUserDirectory>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var pending = await dbContext.Outbox
            .Where(record => record.EventName == EventName)
            .Where(record => !dbContext.Inbox.Any(entry =>
                entry.Consumer == Consumer && entry.MessageId == record.Id))
            .OrderBy(record => record.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var record in pending)
        {
            await DeliverAsync(dbContext, channel, userDirectory, clock, record, cancellationToken);
        }
    }

    private static async Task DeliverAsync(
        NotificationsDbContext dbContext,
        IEmailChannel channel,
        IUserDirectory userDirectory,
        IClock clock,
        OutboxRecord record,
        CancellationToken cancellationToken)
    {
        var export = ParsePayload(record.PayloadJson);
        var email = await userDirectory.GetEmailAsync(export.SubjectId, cancellationToken);
        var notification = Notification.CreateEmail(
            export.TenantId,
            export.SubjectId,
            email ?? string.Empty,
            QuotationsExportReadyEmailTemplate.TemplateRef,
            clock.UtcNow);

        if (string.IsNullOrWhiteSpace(email))
        {
            notification.MarkFailed("recipient_email_unavailable", clock.UtcNow);
        }
        else
        {
            try
            {
                var message = QuotationsExportReadyEmailTemplate.Render(
                    email,
                    export.Kind,
                    export.DownloadUrl,
                    export.FileName,
                    export.RowCount,
                    export.ExpiresAt);
                await channel.SendAsync(message, cancellationToken);
                notification.MarkSent(clock.UtcNow);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                notification.MarkFailed(exception.Message, clock.UtcNow);
            }
        }

        // Se marca el fallo y se escribe el inbox en vez de tirar: una excepción acá aborta el
        // lote entero y lo reintenta para siempre. Misma regla que el worker de invitaciones.
        dbContext.Notifications.Add(notification);
        dbContext.Inbox.Add(new NotificationInboxMessage
        {
            Consumer = Consumer,
            MessageId = record.Id,
            ProcessedAt = clock.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static ReadyPayload ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new ReadyPayload(
            root.GetProperty("tenantId").GetGuid(),
            root.GetProperty("subjectId").GetGuid(),
            root.GetProperty("kind").GetString() ?? string.Empty,
            root.GetProperty("downloadUrl").GetString() ?? string.Empty,
            root.GetProperty("fileName").GetString() ?? string.Empty,
            root.GetProperty("rowCount").GetInt32(),
            root.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private sealed record ReadyPayload(
        Guid TenantId,
        Guid SubjectId,
        string Kind,
        string DownloadUrl,
        string FileName,
        int RowCount,
        DateTimeOffset ExpiresAt);
}
