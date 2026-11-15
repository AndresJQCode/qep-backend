using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Modules.Support.Application;
using Modules.Support.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Soporte fase 2 (plan E11): escribe <c>TicketOpened</c>/<c>TicketClosed</c> en el hilo dentro de la transacción de Soporte, con el
/// mismo enrolamiento que <see cref="CatalogProductDeletionCleanup"/>: un <see cref="MessagingDbContext"/> pasa a la conexión y la
/// transacción del <see cref="SupportDbContext"/> del scope y <see cref="IConversationEventAppender"/> escribe por ahí. Si Soporte no
/// confirma, el evento tampoco queda.
///
/// El <see cref="MessagingDbContext"/> es el de un scope hijo y no el del request: el request ya pudo haberlo usado (leer la conversación,
/// mandar una respuesta por Mensajería) con su propia conexión, y enrolar ese contexto a mitad de camino lo dejaría apuntando a una
/// transacción ajena para el resto del request. El hijo vive sólo lo que dura la escritura.
/// </summary>
internal sealed partial class SupportConversationEvents(
    SupportDbContext support,
    IServiceScopeFactory scopeFactory,
    IMessagingNotifier notifier,
    ILogger<SupportConversationEvents> logger) : ISupportConversationEvents
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Notifying the thread change of conversation {ConversationId} of tenant {TenantId} failed; the inbox polling covers it.")]
    private static partial void LogNotifyFailed(ILogger logger, Exception exception, Guid tenantId, Guid conversationId);

    public async Task AppendAsync(
        Guid tenantId,
        Guid conversationId,
        bool opened,
        Guid ticketId,
        Guid? actorMemberId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var transaction = support.Database.CurrentTransaction
            ?? throw new InvalidOperationException("A ticket thread event must be written inside the support transaction.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var messaging = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        messaging.Database.SetDbConnection(support.Database.GetDbConnection(), contextOwnsConnection: false);
        await messaging.Database.UseTransactionAsync(transaction.GetDbTransaction(), cancellationToken);

        await scope.ServiceProvider.GetRequiredService<IConversationEventAppender>().AppendTicketEventAsync(
            tenantId,
            conversationId,
            opened ? ConversationEventType.TicketOpened : ConversationEventType.TicketClosed,
            ticketId,
            actorMemberId,
            at,
            cancellationToken);
    }

    public async Task NotifyChangedAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        try
        {
            await notifier.ConversationChangedAsync(tenantId, conversationId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogNotifyFailed(logger, exception, tenantId, conversationId);
        }
    }
}
