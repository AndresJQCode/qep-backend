using BuildingBlocks.Application;
using BuildingBlocks.Application.Realtime;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Notifications.Application;

public sealed record MarkAllNotificationsReadCommand(Guid TenantId, string? UpTo) : ICommand<MarkAllReadResultDto>;

public sealed class MarkAllNotificationsReadValidator : AbstractValidator<MarkAllNotificationsReadCommand>
{
    public MarkAllNotificationsReadValidator() =>
        RuleFor(command => command.UpTo)
            .Must(upTo => InAppUpTo.TryParse(upTo, out _))
            .When(command => command.UpTo is not null)
            .OverridePropertyName("upTo")
            .WithMessage("Escribe la fecha en formato ISO 8601 con zona, por ejemplo 2026-11-22T15:04:05Z.");
}

/// <summary>
/// Spec 2026-11-22 §8.4: marks the unread entries at or before <c>upTo</c> (the <c>createdAt</c> of the newest one the screen showed,
/// as received), or all of them without it. <c>upTo</c> exists so an entry that arrived after the person opened the list is not marked
/// read without having been seen.
/// </summary>
public sealed class MarkAllNotificationsReadHandler(
    IExecutionContext executionContext,
    IMembershipDirectory memberships,
    IInAppNotificationRepository repository,
    IRealtimeMemberPublisher publisher,
    IClock clock,
    IValidator<MarkAllNotificationsReadCommand> validator)
    : ICommandHandler<MarkAllNotificationsReadCommand, MarkAllReadResultDto>
{
    public async Task<MarkAllReadResultDto> HandleAsync(MarkAllNotificationsReadCommand command, CancellationToken cancellationToken)
    {
        var memberId = await InAppAuthorization.EnsureMemberAsync(executionContext, memberships, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        DateTimeOffset? upTo = InAppUpTo.TryParse(command.UpTo, out var parsed) ? parsed : null;
        await repository.MarkAllReadAsync(command.TenantId, memberId, upTo, InAppTime.ToMicroseconds(clock.UtcNow), cancellationToken);

        // A null notificationId: not one entry but all of them changed.
        await publisher.PublishToMemberAsync(
            command.TenantId, memberId, InAppNotificationEvents.NotificationsChanged, new { notificationId = (Guid?)null }, CancellationToken.None);

        return new MarkAllReadResultDto(await repository.CountUnreadAsync(command.TenantId, memberId, cancellationToken));
    }
}
