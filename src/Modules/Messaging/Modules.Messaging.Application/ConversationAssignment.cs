using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record TakeConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

public sealed record TransferConversationCommand(Guid TenantId, Guid ConversationId, Guid? MemberId, long ExpectedVersion) : ICommand<ConversationSummary>;

public sealed record ReleaseConversationCommand(Guid TenantId, Guid ConversationId, long ExpectedVersion) : ICommand<ConversationSummary>;

public sealed record ChangeConversationCustomerCommand(Guid TenantId, Guid ConversationId, Guid? CustomerId, long ExpectedVersion)
    : ICommand<ConversationSummary>;

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
    IClock clock,
    IMessagingNotifier notifier)
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
        Guid tenantId, Conversation conversation, IReadOnlyList<ConversationEvent> changes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (changes.Count > 0)
        {
            // Dos eventos del mismo cambio deben leerse en orden; los GUID v7 no lo garantizan dentro del mismo milisegundo,
            // así que el n-ésimo evento lleva n microsegundos más (la precisión de Postgres).
            for (var position = 0; position < changes.Count; position++)
            {
                var change = changes[position];
                events.Record(tenantId, conversation.ConnectionId, conversation.Id, change, now.AddTicks(10 * position));
                audit.Record(tenantId, executionContext.SubjectId, MessagingAuditActions.ForEvent(change.Type), conversation.Id, now);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            // Spec 2026-10-14: después del commit, para que las otras pestañas y el otro asesor relean la asignación.
            await notifier.ConversationChangedAsync(tenantId, conversation.Id, cancellationToken);
        }

        return await summaries.BuildOneAsync(tenantId, (await queries.FindAsync(tenantId, conversation.Id, cancellationToken))!, cancellationToken);
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
        return await steps.CommitAsync(command.TenantId, conversation, conversation.Take(member, now), now, cancellationToken);
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
        var changes = conversation.TransferTo(member, target, now);
        return await steps.CommitAsync(command.TenantId, conversation, changes, now, cancellationToken);
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
        return await steps.CommitAsync(command.TenantId, conversation, ReleasedAsList(conversation.Release(member, now)), now, cancellationToken);
    }

    private static IReadOnlyList<ConversationEvent> ReleasedAsList(ConversationEvent? change) => change is null ? [] : [change];
}

public sealed class ChangeConversationCustomerValidator : AbstractValidator<ChangeConversationCustomerCommand>
{
    public ChangeConversationCustomerValidator() =>
        RuleFor(command => command.CustomerId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("customerId")
            .WithMessage("Elige el cliente de la conversación.");
}

/// <summary>Spec 2026-10-24 §4.2: cambia el cliente principal de la conversación. Autoriza antes de validar (no copia el orden de
/// transferir): tenant, permiso y módulo → conversación (404) → membresía activa → validación → el cliente existe en el tenant
/// (422, igual si es de otro tenant) → If-Match (412) → agregado → evento y auditoría. Sirve cualquier cliente del tenant, activo
/// o no (D-D7). El mismo cliente responde 200 sin evento, auditoría ni versión nueva.
/// <para>Lo que <b>no</b> se mueve (D-D8): el BSUID (sigue siendo del cliente que lo registró), los negocios y el triage del
/// embudo y los mensajes ya enviados. Lo que sigue a la conversación porque lee <c>customer_id</c>: el opt-out por botón y por
/// 131050, el asignado heredero, la búsqueda y el filtro por etiqueta y el triage pendiente.</para></summary>
public sealed class ChangeConversationCustomerHandler(
    ConversationAssignmentSteps steps, IMessagingCustomerDirectory customers, IValidator<ChangeConversationCustomerCommand> validator)
    : ICommandHandler<ChangeConversationCustomerCommand, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(ChangeConversationCustomerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (conversation, member) = await steps.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var customerId = command.CustomerId!.Value;
        var found = await customers.FindRefsAsync(command.TenantId, [customerId], cancellationToken);
        if (!found.ContainsKey(customerId))
        {
            throw new MessagingDomainException(MessagingErrorCodes.ConversationCustomerNotFound, "That customer does not exist in this tenant.");
        }

        ConversationConcurrency.EnsureVersion(conversation, command.ExpectedVersion);
        var now = steps.Now;
        return await steps.CommitAsync(command.TenantId, conversation, conversation.ChangeCustomer(customerId, member, now), now, cancellationToken);
    }
}
