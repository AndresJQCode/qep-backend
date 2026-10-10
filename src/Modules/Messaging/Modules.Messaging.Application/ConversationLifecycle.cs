using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record MarkConversationReadCommand(Guid TenantId, Guid ConversationId) : ICommand<bool>;

public sealed record ResolveConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

public sealed record ReopenConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

/// <summary>§8.4: commit primero; después el acuse a Meta, best effort (5 s), sólo si había no leídos, hay
/// último entrante de menos de 30 días y la conexión está Active. Siempre 204.</summary>
public sealed partial class MarkConversationReadHandler(
    IConversationRepository repository,
    IMessagingUnitOfWork unitOfWork,
    IMessagingConnectionDirectory connections,
    IWhatsAppCloudClient meta,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    ILogger<MarkConversationReadHandler> logger)
    : ICommandHandler<MarkConversationReadCommand, bool>
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Read receipt for conversation {ConversationId} could not be sent to Meta; the conversation is marked read anyway.")]
    private static partial void LogReceiptFailed(ILogger logger, Guid conversationId, Exception? exception);

    public async Task<bool> HandleAsync(MarkConversationReadCommand command, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await repository.FindAsync(command.TenantId, command.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(command.ConversationId);

        var now = clock.UtcNow;
        if (!conversation.MarkRead(now))
        {
            return false;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (!conversation.CanAcknowledgeReading(now))
        {
            return true;
        }

        try
        {
            var sender = await connections.ResolveSenderAsync(command.TenantId, conversation.ConnectionId, cancellationToken);
            if (sender is not null && !await meta.MarkReadAsync(sender, conversation.LastInboundWamid!, cancellationToken))
            {
                LogReceiptFailed(logger, conversation.Id, null);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReceiptFailed(logger, conversation.Id, exception);
        }

        return true;
    }
}

/// <summary>§8.5: por el agregado con If-Match (428 lo pone el endpoint; 412 lo pone el unit of work).</summary>
public sealed class ResolveConversationHandler(
    IConversationRepository repository,
    IConversationQueries queries,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ConversationSummaryBuilder summaries,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ResolveConversationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(ResolveConversationCommand command, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await repository.FindAsync(command.TenantId, command.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(command.ConversationId);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);

        var now = clock.UtcNow;
        conversation.Resolve(now);
        audit.Record(command.TenantId, executionContext.SubjectId, MessagingAuditActions.Resolved, conversation.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (await summaries.BuildAsync(command.TenantId, [(await queries.FindAsync(command.TenantId, conversation.Id, cancellationToken))!], cancellationToken))[0];
    }
}

public sealed class ReopenConversationHandler(
    IConversationRepository repository,
    IConversationQueries queries,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ConversationSummaryBuilder summaries,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ReopenConversationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(ReopenConversationCommand command, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await repository.FindAsync(command.TenantId, command.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(command.ConversationId);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);

        var now = clock.UtcNow;
        conversation.Reopen(now);
        audit.Record(command.TenantId, executionContext.SubjectId, MessagingAuditActions.Reopened, conversation.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (await summaries.BuildAsync(command.TenantId, [(await queries.FindAsync(command.TenantId, conversation.Id, cancellationToken))!], cancellationToken))[0];
    }
}

/// <summary>Copia de <c>ConcurrencyGuard</c> de Integrations: la versión del If-Match tiene que ser la vigente.</summary>
internal static class ConversationConcurrency
{
    public static void EnsureVersion(Conversation conversation, long expectedVersion)
    {
        if (conversation.Version != expectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The conversation changed since it was loaded.");
        }
    }
}
