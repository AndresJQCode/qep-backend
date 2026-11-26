using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <summary>El cuerpo de <c>POST /conversations/{conversationId}/tickets</c>: el solicitante, el canal y la entrada salen de la
/// conversación (plan E10), así que sólo viaja lo que el asesor elige. Todo anulable para que su ausencia sea un
/// <c>validation.failed</c> con el campo; sin <c>Priority</c> el ticket nace en <c>Medium</c> (spec 2026-11-15 §5.3).</summary>
public sealed record CreateTicketFromConversationRequest(
    Guid? TicketTypeId, string? Subject, string? Description, Guid? AssigneeMemberId, string? Priority = null);

/// <param name="Priority">El nombre exacto del cuerpo; <c>null</c> es <c>Medium</c>.</param>
public sealed record CreateTicketFromConversationCommand(
    Guid TenantId,
    Guid ConversationId,
    Guid TicketTypeId,
    string Subject,
    string Description,
    Guid? AssigneeMemberId,
    string? Priority = null)
    : ICommand<SupportTicketDetailDto>
{
    public static CreateTicketFromConversationCommand From(
        Guid tenantId, Guid conversationId, CreateTicketFromConversationRequest request) => new(
        tenantId,
        conversationId,
        request.TicketTypeId ?? Guid.Empty,
        request.Subject ?? string.Empty,
        request.Description ?? string.Empty,
        request.AssigneeMemberId,
        request.Priority);
}

/// <summary>Cubre las guardas de <see cref="SupportTicket.CreateFromConversation"/> que vienen del cuerpo: ningún cuerpo que lo pase
/// llega al dominio a lanzar.</summary>
public sealed class CreateTicketFromConversationValidator : AbstractValidator<CreateTicketFromConversationCommand>
{
    public CreateTicketFromConversationValidator()
    {
        RuleFor(command => command.TicketTypeId).NotEqual(Guid.Empty);
        RuleFor(command => command.Subject).MustFit(1, SupportTicket.SubjectMaxLength);
        RuleFor(command => command.Description).MustFit(1, SupportTicket.DescriptionMaxLength);
        RuleFor(command => command.Priority).MustBeAPriority();
        RuleFor(command => command.AssigneeMemberId).NotEqual(Guid.Empty);
    }
}

/// <summary>
/// Abrir un ticket desde una conversación (spec §8.1, plan E10 y E11). Orden: autorización → membresía → validador → módulo
/// <c>messaging</c> (403) → conversación del tenant (404) → foto del solicitante → provisión → tipo activo → cliente → responsable →
/// transacción → candado del cliente → radicado → contexto → ticket → clave de consulta (sólo el hash del PIN) → auditoría →
/// <c>TicketOpened</c> en el hilo → guardar → confirmar → aviso al tiempo real → información con el PIN por el hilo. Como el alta manual,
/// todo lo que puede responder 422 va antes de la transacción.
/// El cliente es el de la conversación sólo si <c>customers</c> está prendido y el cliente está activo; si no, el ticket nace sin cliente
/// y con la foto que da la conversación. Fase 3 (plan F25): después de confirmar se le manda al contacto, por el hilo y como texto, el
/// radicado con el PIN si la ventana de 24 h está abierta; si no, nada (<c>window_closed</c> en <c>ConsultAccess</c>). El ticket queda
/// creado pase lo que pase con el envío, y el envío no le sube la versión (<see cref="SupportOpeningDelivery"/>).
/// </summary>
public sealed class CreateTicketFromConversationHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantDirectory tenantDirectory,
    IMembershipDirectory membershipDirectory,
    ISupportConversationDirectory conversations,
    ISupportProvisioner provisioner,
    ISupportTicketTypeRepository types,
    ISupportTicketRepository tickets,
    ISupportTicketAccessRepository accesses,
    ISupportPinHasher pinHasher,
    ISupportCustomerLookup customers,
    ISupportMemberDirectory members,
    ISupportTicketNumberGenerator numbers,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportIntegrationEvents integrationEvents,
    ISupportRealtime realtime,
    ISupportConversationEvents conversationEvents,
    SupportTicketDtoComposer composer,
    SupportOpeningDelivery openingDelivery,
    ISupportPublicOptions publicOptions,
    IValidator<CreateTicketFromConversationCommand> validator)
    : ICommandHandler<CreateTicketFromConversationCommand, SupportTicketDetailDto>
{
    private static readonly string[] CreatedFields =
        ["ticketTypeId", "subject", "description", "conversationId", "assigneeMemberId", "priority"];

    public async Task<SupportTicketDetailDto> HandleAsync(
        CreateTicketFromConversationCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.Manage, cancellationToken);
        var actor = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw SupportAuthorization.Denied();
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, command.TenantId, TenantModuleKeys.Messaging, cancellationToken);
        var conversation = await SupportConversationRules.EnsureConversationAsync(
            conversations, command.TenantId, command.ConversationId, cancellationToken);

        var customer = await ActiveCustomerAsync(command.TenantId, conversation.CustomerId, cancellationToken);
        var tenantCountry = (await tenantDirectory.GetRegionalSettingsAsync(new TenantId(command.TenantId), cancellationToken)
            ?? throw new InvalidOperationException($"Tenant '{command.TenantId}' has no regional settings.")).Country;
        var requester = SupportConversationRules.RequesterFor(conversation, customer, tenantCountry);
        SupportTicketRules.EnsureRequester(customer?.Id, requester);

        await provisioner.EnsureAsync(command.TenantId, cancellationToken);
        var type = await SupportTicketRules.EnsureTypeAsync(types, command.TenantId, command.TicketTypeId, cancellationToken);
        await SupportTicketRules.EnsureAssigneeAsync(members, command.TenantId, command.AssigneeMemberId, cancellationToken);
        var slug = await SupportOpeningDelivery.SlugAsync(tenantDirectory, command.TenantId, cancellationToken);

        var pin = SupportPin.Generate();
        SupportReadContext context;
        SupportTicket ticket;
        SupportTicketAccess access;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            // El mismo orden de candados que el alta manual: el del cliente antes que la fila del radicado.
            if (customer is not null)
            {
                await unitOfWork.HoldCustomerReferenceAsync(command.TenantId, customer.Id, cancellationToken);
            }

            var number = await numbers.NextAsync(command.TenantId, cancellationToken);
            context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
            var now = context.Now;
            ticket = SupportTicket.CreateFromConversation(
                SupportTicketId.New(),
                command.TenantId,
                number,
                type,
                command.Subject,
                command.Description,
                SupportPriorityRules.Parse(command.Priority),
                new SupportConversationRef(conversation.Id, conversation.Channel),
                customer?.Id,
                requester,
                command.AssigneeMemberId,
                actor,
                now,
                context.Clock);
            tickets.Add(ticket);
            // Same rule as the manual creation: an assignee other than the actor is told, in this transaction.
            SupportAssignmentNotices.PublishOnCreate(integrationEvents, ticket, actor, now);
            // Plan F25: la clave de consulta nace con el ticket. Sólo su hash: el PIN viaja una vez, por el hilo, y no va a la
            // auditoría ni a la traza.
            access = SupportTicketAccess.Issue(ticket.Id, command.TenantId, pinHasher.Hash(pin), now);
            accesses.Add(access);
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                SupportAuditActions.TicketCreated,
                SupportAuditActions.Resources.Ticket,
                ticket.Id.ToString(),
                "success",
                CreatedFields,
                now);
            await conversationEvents.AppendAsync(
                command.TenantId, conversation.Id, opened: true, ticket.Id.Value, actor, now, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await realtime.TicketChangedAsync(command.TenantId, ticket.Id.Value, CancellationToken.None);
        await conversationEvents.NotifyChangedAsync(command.TenantId, conversation.Id, cancellationToken);
        await openingDelivery.DeliverAsync(
            ticket, access, pin, SupportOpeningSender.ConsultUrl(publicOptions, slug, ticket.DisplayNumber), context.Now,
            cancellationToken);
        return await composer.ComposeDetailAsync(ticket, context, cancellationToken);
    }

    // Plan E10: el cliente de la conversación entra sólo si se puede vincular; si no, el ticket nace sin cliente en vez de responder
    // 403 o 422 por algo que el asesor no eligió.
    private async Task<SupportCustomerContact?> ActiveCustomerAsync(
        Guid tenantId, Guid? customerId, CancellationToken cancellationToken)
    {
        if (customerId is not { } id
            || !await SupportConversationRules.IsEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Customers, cancellationToken))
        {
            return null;
        }

        return await customers.FindAsync(tenantId, id, cancellationToken) is { IsActive: true } customer ? customer : null;
    }
}
