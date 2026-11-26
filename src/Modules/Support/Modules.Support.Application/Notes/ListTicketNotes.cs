using BuildingBlocks.Application;
using FluentValidation;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <param name="Before">The <see cref="SupportMessageCursorDto.Before"/> of the previous page; null for the first one.</param>
public sealed record ListTicketNotesQuery(Guid TenantId, Guid TicketId, string? Before, int Take) : IQuery<SupportTicketNotesDto>
{
    public const int DefaultTake = 50;

    public const int MaxTake = 100;
}

public sealed class ListTicketNotesValidator : AbstractValidator<ListTicketNotesQuery>
{
    public ListTicketNotesValidator()
    {
        RuleFor(query => query.Take).InclusiveBetween(1, ListTicketNotesQuery.MaxTake);
        RuleFor(query => query.Before)
            .Must(text => SupportMessageCursor.TryDecode(text, out _))
            .When(query => query.Before is not null)
            .WithMessage("Before must be the cursor of a previous page.");
    }
}

/// <summary>
/// Spec 2026-11-15 §5.4 (plan N22): a ticket's internal notes, newest first, with the same opaque cursor as the ticket's file
/// (<see cref="SupportMessageCursor"/> over <c>(created_at, id)</c>). Reading needs <c>support.ticket.view</c> (read or manage).
/// </summary>
public sealed class ListTicketNotesHandler(
    SupportTicketScope scope,
    ISupportTicketNoteRepository notes,
    ISupportMemberDirectory members,
    ISupportViewer viewer,
    IValidator<ListTicketNotesQuery> validator)
    : IQueryHandler<ListTicketNotesQuery, SupportTicketNotesDto>
{
    public async Task<SupportTicketNotesDto> HandleAsync(ListTicketNotesQuery query, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(query.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        var ticketId = new SupportTicketId(query.TicketId);
        _ = await scope.ViewAsync(query.TenantId, query.TicketId, cancellationToken);

        (DateTimeOffset CreatedAt, Guid Id)? before = SupportMessageCursor.TryDecode(query.Before, out var cursor) ? cursor : null;
        // One more than the page, to know whether another follows without counting.
        var page = await notes.ListAsync(query.TenantId, ticketId, before, query.Take + 1, cancellationToken);
        var visible = page.Take(query.Take).ToList();
        var items = await SupportNoteDtos.ComposeAsync(members, viewer, query.TenantId, visible, cancellationToken);
        var next = page.Count > query.Take && visible.Count > 0
            ? new SupportMessageCursorDto(SupportMessageCursor.Encode(visible[^1].CreatedAt, visible[^1].Id))
            : null;
        return new SupportTicketNotesDto(items, next);
    }
}
