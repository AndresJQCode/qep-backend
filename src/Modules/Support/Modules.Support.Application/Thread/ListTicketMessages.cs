using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <param name="Before">El cursor de <see cref="SupportMessageCursorDto.Before"/> de la página anterior; nulo, la primera.</param>
public sealed record ListTicketMessagesQuery(Guid TenantId, Guid TicketId, string? Before, int Take) : IQuery<SupportTicketMessagesDto>
{
    public const int DefaultTake = 50;

    public const int MaxTake = 100;
}

public sealed class ListTicketMessagesValidator : AbstractValidator<ListTicketMessagesQuery>
{
    public ListTicketMessagesValidator()
    {
        RuleFor(query => query.Take).InclusiveBetween(1, ListTicketMessagesQuery.MaxTake);
        RuleFor(query => query.Before)
            .Must(text => SupportMessageCursor.TryDecode(text, out _))
            .When(query => query.Before is not null)
            .WithMessage("Before must be the cursor of a previous page.");
    }
}

/// <summary>
/// El expediente del ticket (spec §8.2, plan E9 y E20): los mensajes vinculados y fijados, nunca los soltados, del más nuevo al más
/// viejo, con su texto. No exige permisos de Mensajería: mostrar el expediente es de Soporte. Un ticket sin conversación tiene el
/// expediente vacío.
/// </summary>
public sealed class ListTicketMessagesHandler(
    SupportTicketScope scope,
    ISupportTicketMessageRepository links,
    ISupportConversationDirectory conversations,
    ISupportMemberDirectory members,
    IValidator<ListTicketMessagesQuery> validator)
    : IQueryHandler<ListTicketMessagesQuery, SupportTicketMessagesDto>
{
    public async Task<SupportTicketMessagesDto> HandleAsync(ListTicketMessagesQuery query, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(query.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        var ticket = await scope.ViewAsync(query.TenantId, query.TicketId, cancellationToken);
        if (ticket.ConversationId is not { } conversationId)
        {
            return new SupportTicketMessagesDto([], null);
        }

        (DateTimeOffset OccurredAt, Guid MessageId)? before =
            SupportMessageCursor.TryDecode(query.Before, out var cursor) ? cursor : null;
        // Uno de más para saber si hay otra página sin contarlas.
        var page = await links.ListVisibleAsync(query.TenantId, ticket.Id, before, query.Take + 1, cancellationToken);
        var visible = page.Take(query.Take).ToList();
        var messages = await SupportThreadRules.ReadMessagesAsync(
            conversations, members, query.TenantId, conversationId, visible.Select(link => link.MessageId).ToList(), cancellationToken);

        var items = visible
            .Where(link => messages.ContainsKey(link.MessageId))
            .Select(link => new SupportTicketMessageDto(messages[link.MessageId], link.State.ToString()))
            .ToList();
        var next = page.Count > query.Take && visible.Count > 0
            ? new SupportMessageCursorDto(SupportMessageCursor.Encode(visible[^1].OccurredAt, visible[^1].MessageId))
            : null;
        return new SupportTicketMessagesDto(items, next);
    }
}
