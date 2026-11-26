using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-11-17 (abrir la conversación del cliente): <c>{ conversation, created }</c>.</summary>
public sealed record OpenedCustomerConversationDto(ConversationSummary Conversation, bool Created);

/// <summary><c>Created</c> decide el status: 201 si la conversación se creó, 200 si ya existía.</summary>
public sealed record OpenCustomerConversationResult(OpenedCustomerConversationDto Body, bool Created);

public sealed record OpenCustomerConversationCommand(Guid TenantId, Guid? CustomerId, Guid? ConnectionId) : ICommand<OpenCustomerConversationResult>;

public sealed class OpenCustomerConversationValidator : AbstractValidator<OpenCustomerConversationCommand>
{
    public OpenCustomerConversationValidator() =>
        RuleFor(command => command.CustomerId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("customerId");
}

/// <summary>
/// Spec 2026-11-17: la conversación de WhatsApp del cliente en una conexión, sin enviar nada. Validador → tenant, permiso y módulo →
/// membresía → conexión (con <c>connectionId</c>: Instagram 422, no Active 422; sin él: la única de WhatsApp Active, ninguna 422
/// <c>connection_unavailable</c>, varias 422 <c>connection_required</c>) → cliente (404 / 422) → <see cref="IConversationOpener"/> (una
/// creada llega asignada a quien llama, con su <c>Taken</c>) → si estaba resuelta se reabre → si no tenía asesor la toma quien llama. Una
/// asignada a otro se devuelve como está: tomarla es «Tomar conversación», a la vista del otro asesor.
/// </summary>
public sealed class OpenCustomerConversationHandler(
    IMessagingConnectionDirectory connections,
    IMessagingCustomerDirectory customers,
    IConversationOpener opener,
    IConversationRepository repository,
    ConversationAssignmentSteps steps,
    CallerMembership caller,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IAutomationInbox inbox,
    IBotDelegationRepository delegations,
    IValidator<OpenCustomerConversationCommand> validator)
    : ICommandHandler<OpenCustomerConversationCommand, OpenCustomerConversationResult>
{
    public async Task<OpenCustomerConversationResult> HandleAsync(OpenCustomerConversationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var tenantId = command.TenantId;
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, tenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var member = await caller.FindAsync(tenantId, cancellationToken)
            ?? throw new RequestForbiddenException("authorization.denied", "The subject does not have an active membership in this tenant.");

        // La conexión antes que el cliente, como en StartConversationHandler: un cliente que sólo tiene Instagram no es «inalcanzable».
        var connectionId = await ResolveConnectionAsync(tenantId, command.ConnectionId, cancellationToken);

        // Todo 404/422 antes de abrir: un cliente al que no se le puede escribir no deja una conversación vacía.
        var contact = await customers.FindContactAsync(tenantId, command.CustomerId!.Value, cancellationToken);
        if (contact is not { IsActive: true })
        {
            throw new ResourceNotFoundException(MessagingErrorCodes.CustomerNotFound, $"Customer '{command.CustomerId}' was not found.");
        }

        if (contact.PhoneE164 is null && contact.WhatsAppUserId is null)
        {
            throw new MessagingDomainException(MessagingErrorCodes.CustomerUnreachable, "The customer has neither a phone number nor a WhatsApp user id.");
        }

        var now = steps.Now;
        var opened = await opener.OpenAsync(tenantId, connectionId, contact, member, executionContext.SubjectId, now, cancellationToken);
        var conversation = await repository.FindAsync(tenantId, opened.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(opened.ConversationId);

        var changes = new List<ConversationEvent>();
        // Decisión del owner 2026-11-17: el hilo bloquea el compositor de una resuelta, y el botón tiene que dejar listo para escribir.
        // Misma transición, evento y auditoría que POST …/reopen.
        if (conversation.Status == ConversationStatus.Resolved)
        {
            conversation.Reopen(now);
            changes.Add(new ConversationEvent(ConversationEventType.Reopened, Actor: member));
        }

        if (conversation.AssignedMemberId is null)
        {
            // Como TakeConversationHandler: si un bot la atendía, tomarla lo pausa y la corrida viva recibe la señal en el mismo guardado.
            var before = conversation.AutomationStatus;
            var beforeBot = conversation.AutomationBotKind;
            var beforeDelegation = conversation.AutomationDelegationId;
            var taken = conversation.Take(member, now);
            await inbox.EnqueueIfAssistedAsync(delegations, conversation, before, beforeBot, beforeDelegation, taken.Count, now, cancellationToken);
            changes.AddRange(taken);
        }

        var summary = await steps.CommitAsync(tenantId, conversation, changes, now, cancellationToken);
        return new OpenCustomerConversationResult(new OpenedCustomerConversationDto(summary, opened.Created), opened.Created);
    }

    private async Task<Guid> ResolveConnectionAsync(Guid tenantId, Guid? requested, CancellationToken cancellationToken)
    {
        if (requested is { } connectionId)
        {
            // Spec 2026-10-14 §3: en Instagram no se inicia una conversación.
            if (await connections.FindChannelAsync(tenantId, connectionId, cancellationToken) == ConversationChannel.Instagram)
            {
                throw new MessagingDomainException(MessagingErrorCodes.ChannelUnsupported, "A conversation cannot be started on Instagram.");
            }

            if (await connections.ResolveSenderAsync(tenantId, connectionId, cancellationToken) is null)
            {
                throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "The WhatsApp connection is paused, needs attention or was deleted.");
            }

            return connectionId;
        }

        var whatsApp = (await connections.ListActiveAsync(tenantId, cancellationToken))
            .Where(connection => connection.Channel == nameof(ConversationChannel.WhatsApp))
            .ToList();
        return whatsApp.Count switch
        {
            0 => throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "The tenant has no active WhatsApp connection."),
            1 => whatsApp[0].Id,
            // Decisión del owner 2026-11-17: con varios números el asesor elige, aunque el cliente ya tenga hilo en uno de ellos.
            _ => throw new MessagingDomainException(MessagingErrorCodes.ConnectionRequired, "The tenant has several WhatsApp connections; choose one."),
        };
    }
}
