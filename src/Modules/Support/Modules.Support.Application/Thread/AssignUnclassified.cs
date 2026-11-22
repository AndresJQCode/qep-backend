using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary>El cuerpo de asignar: el ticket elegido y su versión. La versión va en el cuerpo y no en <c>If-Match</c> porque la ruta es de
/// la conversación, no del ticket.</summary>
public sealed record AssignUnclassifiedRequest(Guid? TicketId, long? Version);

public sealed record AssignUnclassifiedCommand(Guid TenantId, Guid ConversationId, Guid MessageId, Guid TicketId, long ExpectedVersion)
    : ICommand<SupportUnclassifiedDto>
{
    public static AssignUnclassifiedCommand From(Guid tenantId, Guid conversationId, Guid messageId, AssignUnclassifiedRequest request) =>
        new(tenantId, conversationId, messageId, request.TicketId ?? Guid.Empty, request.Version ?? 0);
}

public sealed class AssignUnclassifiedValidator : AbstractValidator<AssignUnclassifiedCommand>
{
    public AssignUnclassifiedValidator()
    {
        RuleFor(command => command.TicketId).NotEqual(Guid.Empty);
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Asignar un entrante sin clasificar a uno de sus tickets (spec §8.2, plan E8): la fila queda <c>Assigned</c> y el mensaje
/// <see cref="SupportTicketMessageState.Pinned"/> en el elegido, con <c>MessageClassified</c>; si el elegido esperaba al cliente, se
/// reabre como en el seguimiento (plan E6). Fila que ya no está pendiente: 422 <c>support.unclassified.not_pending</c>; ticket que no es
/// un candidato abierto de esa conversación: 422 <c>support.unclassified.ticket_not_candidate</c>; versión vieja: 412.
/// </summary>
public sealed class AssignUnclassifiedHandler(
    IExecutionContext executionContext,
    SupportTicketScope scope,
    ISupportConversationDirectory conversations,
    ISupportTicketRepository tickets,
    ISupportTicketMessageRepository links,
    ISupportUnclassifiedRepository unclassified,
    ISupportMemberDirectory members,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportIntegrationEvents integrationEvents,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer,
    IValidator<AssignUnclassifiedCommand> validator)
    : ICommandHandler<AssignUnclassifiedCommand, SupportUnclassifiedDto>
{
    private static readonly string[] ChangedFields = ["resolution", "assignedTicketId"];

    public async Task<SupportUnclassifiedDto> HandleAsync(AssignUnclassifiedCommand command, CancellationToken cancellationToken)
    {
        // La ruta exige manage; FindManagedAsync lo vuelve a exigir. Un ticket inexistente o interno es candidato inválido (422), no 404.
        var access = await scope.EnsureAccessAsync(command.TenantId, cancellationToken);
        var actor = access.MemberId ?? throw SupportAuthorization.Denied();
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var conversation = await SupportConversationRules.EnsureConversationAsync(
            conversations, command.TenantId, command.ConversationId, cancellationToken);
        var row = await UnclassifiedRules.EnsurePendingAsync(unclassified, command.TenantId, conversation.Id, command.MessageId, cancellationToken);

        var ticket = await scope.FindManagedAsync(command.TenantId, command.TicketId, cancellationToken);
        if (ticket is null || ticket.ConversationId != conversation.Id || !ticket.CoversInbound(row.OccurredAt))
        {
            throw new SupportDomainException(
                SupportErrorCodes.TicketNotCandidate, "The ticket is not an open candidate for this message.");
        }

        if (ticket.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The ticket changed after it was loaded.");
        }

        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var now = context.Now;
        row.Assign(ticket.Id, actor, now);
        var link = await links.FindAsync(command.TenantId, ticket.Id, row.MessageId, cancellationToken);
        if (link is null)
        {
            links.Add(SupportTicketMessage.Link(
                command.TenantId, ticket.Id, conversation.Id, row.MessageId, row.OccurredAt, SupportTicketMessageState.Pinned, now));
        }
        else
        {
            link.Pin(now);
        }

        ticket.RecordMessageEvent(SupportTicketEventKind.MessageClassified, row.MessageId, actor, now);
        SupportThreadRules.ReopenIfWaiting(ticket, context, auditPublisher, integrationEvents, actor);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            SupportAuditActions.UnclassifiedAssigned,
            SupportAuditActions.Resources.UnclassifiedMessage,
            row.MessageId.ToString(),
            "success",
            ChangedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await realtime.TicketChangedAsync(command.TenantId, ticket.Id.Value, CancellationToken.None);

        return await UnclassifiedReader.ReadAsync(
            conversations, tickets, unclassified, members, command.TenantId, conversation.Id, cancellationToken);
    }
}

internal static class UnclassifiedRules
{
    /// <summary>La fila pendiente de ese mensaje en esa conversación; si no hay o ya se resolvió, 422
    /// <c>support.unclassified.not_pending</c>.</summary>
    public static async Task<SupportUnclassifiedMessage> EnsurePendingAsync(
        ISupportUnclassifiedRepository unclassified, Guid tenantId, Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        var row = await unclassified.FindAsync(tenantId, messageId, cancellationToken);
        return row is { IsPending: true } && row.ConversationId == conversationId
            ? row
            : throw new SupportDomainException(
                SupportErrorCodes.UnclassifiedNotPending, "The message is not pending classification in this conversation.");
    }
}
