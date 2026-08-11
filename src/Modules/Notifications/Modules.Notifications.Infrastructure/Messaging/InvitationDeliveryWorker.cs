using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BuildingBlocks.Application;
using Modules.Identity.Application;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.Messaging;

// Consume del Outbox de plataforma el evento de integración de membresía invitada y
// entrega el email de invitación. Es idempotente por el inbox propio de este módulo, con
// clave (consumidor, id de mensaje de outbox): un mensaje reentregado se saltea. Cada
// mensaje se commitea independiente, así que una falla no bloquea el lote.
internal sealed partial class InvitationDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationsOptions> options,
    ILogger<InvitationDeliveryWorker> logger) : BackgroundService
{
    private const string Consumer = "notifications.invitation-email";
    private const string EventName = "tenancy.membership-invited.v1";
    private const int BatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    [LoggerMessage(Level = LogLevel.Error, Message = "Invitation delivery tick failed.")]
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

    private async Task DeliverAsync(
        NotificationsDbContext dbContext,
        IEmailChannel channel,
        IUserDirectory userDirectory,
        IClock clock,
        OutboxRecord record,
        CancellationToken cancellationToken)
    {
        var (userId, tenantId) = ParsePayload(record.PayloadJson);
        var email = await userDirectory.GetEmailAsync(userId, cancellationToken);
        var notification = Notification.CreateEmail(
            tenantId,
            userId,
            email ?? string.Empty,
            InvitationEmailTemplate.TemplateRef,
            clock.UtcNow);

        if (string.IsNullOrWhiteSpace(email))
        {
            notification.MarkFailed("recipient_email_unavailable", clock.UtcNow);
        }
        else
        {
            try
            {
                var message = InvitationEmailTemplate.Render(email, tenantId, options.Value.LoginUrl);
                await channel.SendAsync(message, cancellationToken);
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

    private static (Guid UserId, Guid TenantId) ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        var userId = root.GetProperty("userId").GetGuid();
        var tenantId = root.GetProperty("tenantId").GetProperty("value").GetGuid();
        return (userId, tenantId);
    }
}
