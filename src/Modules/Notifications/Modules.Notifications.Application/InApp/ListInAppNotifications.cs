using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Notifications.Application;

public sealed record ListInAppNotificationsQuery(Guid TenantId, string? Cursor, int Limit, bool UnreadOnly)
    : IQuery<InAppNotificationPageDto>
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
}

/// <summary>The field names are the ones the screen reads in <c>errors</c> (spec §8.1), not the C# ones.</summary>
public sealed class ListInAppNotificationsValidator : AbstractValidator<ListInAppNotificationsQuery>
{
    public ListInAppNotificationsValidator()
    {
        RuleFor(query => query.Limit)
            .InclusiveBetween(1, ListInAppNotificationsQuery.MaxLimit)
            .OverridePropertyName("limit")
            .WithMessage($"El límite debe estar entre 1 y {ListInAppNotificationsQuery.MaxLimit}.");
        RuleFor(query => query.Cursor)
            .Must(cursor => InAppCursor.TryParse(cursor, out _))
            .When(query => query.Cursor is not null)
            .OverridePropertyName("cursor")
            .WithMessage("El cursor no es válido. Vuelve a cargar la lista.");
    }
}

/// <summary>Spec 2026-11-22 §8.1: the caller's entries, newest first, with the unread count in the same answer (BFF).</summary>
public sealed class ListInAppNotificationsHandler(
    IExecutionContext executionContext,
    IMembershipDirectory memberships,
    IInAppNotificationRepository repository,
    IInAppNotificationNames names,
    IClock clock,
    IValidator<ListInAppNotificationsQuery> validator)
    : IQueryHandler<ListInAppNotificationsQuery, InAppNotificationPageDto>
{
    public async Task<InAppNotificationPageDto> HandleAsync(ListInAppNotificationsQuery query, CancellationToken cancellationToken)
    {
        var memberId = await InAppAuthorization.EnsureMemberAsync(executionContext, memberships, query.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        (DateTimeOffset, Guid)? after = InAppCursor.TryParse(query.Cursor, out var parsed) ? parsed : null;
        // One more than the page, only to know whether there is a next one.
        var found = await repository.ListAsync(query.TenantId, memberId, query.UnreadOnly, after, query.Limit + 1, cancellationToken);
        var page = found.Take(query.Limit).ToList();
        var nextCursor = found.Count > query.Limit ? InAppCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null;

        var items = await InAppNotificationMapper.ToDtosAsync(page, query.TenantId, names, clock.UtcNow, cancellationToken);
        var unread = await repository.CountUnreadAsync(query.TenantId, memberId, cancellationToken);
        return new InAppNotificationPageDto(items, nextCursor, unread);
    }
}
