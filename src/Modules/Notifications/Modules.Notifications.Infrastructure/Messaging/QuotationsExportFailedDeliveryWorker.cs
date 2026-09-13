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

// Consume `quotations.export-failed.v1`: a quien pidió la exportación le avisa que no le va a
// llegar el archivo y que puede pedirlo de nuevo. Mismo mecanismo que su par de "lista".
internal sealed partial class QuotationsExportFailedDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<QuotationsExportFailedDeliveryWorker> logger) : BackgroundService
{
    private const string Consumer = "notifications.quotations-export-failed-email";
    private const string EventName = "quotations.export-failed.v1";
    private const int BatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [LoggerMessage(Level = LogLevel.Error, Message = "Quotations export failed delivery tick failed.")]
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
            QuotationsExportFailedEmailTemplate.TemplateRef,
            clock.UtcNow);

        if (string.IsNullOrWhiteSpace(email))
        {
            notification.MarkFailed("recipient_email_unavailable", clock.UtcNow);
        }
        else
        {
            try
            {
                await channel.SendAsync(
                    QuotationsExportFailedEmailTemplate.Render(email, export.Kind), cancellationToken);
                notification.MarkSent(clock.UtcNow);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                notification.MarkFailed(exception.Message, clock.UtcNow);
            }
        }

        dbContext.Notifications.Add(notification);
        dbContext.Inbox.Add(new NotificationInboxMessage
        {
            Consumer = Consumer,
            MessageId = record.Id,
            ProcessedAt = clock.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static FailedPayload ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new FailedPayload(
            root.GetProperty("tenantId").GetGuid(),
            root.GetProperty("subjectId").GetGuid(),
            root.GetProperty("kind").GetString() ?? string.Empty);
    }

    private sealed record FailedPayload(Guid TenantId, Guid SubjectId, string Kind);
}
