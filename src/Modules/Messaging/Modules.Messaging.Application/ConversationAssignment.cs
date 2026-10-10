using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record TakeConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

public sealed record TransferConversationCommand(Guid TenantId, Guid ConversationId, Guid? MemberId, long ExpectedVersion) : ICommand<ConversationSummary>;

public sealed record ReleaseConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

public sealed class TransferConversationValidator : AbstractValidator<TransferConversationCommand>
{
    public TransferConversationValidator() =>
        RuleFor(command => command.MemberId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("memberId")
            .WithMessage("Elige a quién transferir la conversación.");
}

/// <summary>Spec 2026-10-10 §8.4, el orden de los tres: tenant, permiso y módulo → conversación del tenant (404) →
/// membresía activa de quien llama (403) → [transfer: puede responder (422)] → If-Match (412) → agregado → evento y
/// auditoría en la misma transacción → 200 ConversationSummary. Sin cambio (D-A2, P8): 200 sin subir la versión.</summary>
public sealed class ConversationAssignmentSteps(
    IConversationRepository repository,
    IConversationQueries queries,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    IConversationEvents events,
    ConversationSummaryBuilder summaries,
    CallerMembership caller,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
{
    public async Task<(Conversation Conversation, Guid Member)> LoadAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, tenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await repository.FindAsync(tenantId, conversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(conversationId);
        var member = await caller.FindAsync(tenantId, cancellationToken)
            ?? throw new RequestForbiddenException("authorization.denied", "The subject does not have an active membership in this tenant.");
        return (conversation, member);
    }

    public DateTimeOffset Now => clock.UtcNow;

    public async Task<ConversationSummary> CommitAsync(
        Guid tenantId, Conversation conversation, ConversationEvent? change, string auditAction, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (change is not null)
        {
            events.Record(tenantId, conversation.ConnectionId, conversation.Id, change, now);
            audit.Record(tenantId, executionContext.SubjectId, auditAction, conversation.Id, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return (await summaries.BuildAsync(tenantId, [(await queries.FindAsync(tenantId, conversation.Id, cancellationToken))!], cancellationToken))[0];
    }
}

public sealed class TakeConversationHandler(ConversationAssignmentSteps steps) : ICommandHandler<TakeConversationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(TakeConversationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (conversation, member) = await steps.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);
        var now = steps.Now;
        return await steps.CommitAsync(command.TenantId, conversation, conversation.Take(member, now), MessagingAuditActions.Taken, now, cancellationToken);
    }
}

public sealed class TransferConversationHandler(ConversationAssignmentSteps steps, IMessagingAssignees assignees, IValidator<TransferConversationCommand> validator)
    : ICommandHandler<TransferConversationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(TransferConversationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var (conversation, member) = await steps.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        var target = command.MemberId!.Value;
        // §11: una membresía de otro tenant responde igual que una inexistente o sin manage; no confirma nada.
        if (!await assignees.CanReplyAsync(command.TenantId, target, cancellationToken))
        {
            throw new MessagingDomainException(MessagingErrorCodes.AssigneeCannotReply, "That member cannot answer conversations in this tenant.");
        }

        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);
        var now = steps.Now;
        var change = conversation.TransferTo(member, target, now);
        var action = change?.Type == ConversationEventType.Taken ? MessagingAuditActions.Taken : MessagingAuditActions.Transferred;
        return await steps.CommitAsync(command.TenantId, conversation, change, action, now, cancellationToken);
    }
}

public sealed class ReleaseConversationHandler(ConversationAssignmentSteps steps) : ICommandHandler<ReleaseConversationCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(ReleaseConversationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (conversation, member) = await steps.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);
        var now = steps.Now;
        return await steps.CommitAsync(command.TenantId, conversation, conversation.Release(member, now), MessagingAuditActions.Released, now, cancellationToken);
    }
}
