using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <param name="Phone">The effective phone: the member's own, or the HR one when there is none.</param>
/// <param name="PhoneSource"><c>Member</c>, <c>Hr</c> or <c>null</c>: where <paramref name="Phone"/> comes from.</param>
public sealed record MyMembershipDto(Guid MembershipId, string? Phone, string? PhoneSource);

public sealed record GetMyMembershipQuery(TenantId TenantId) : IQuery<MyMembershipDto>;

public sealed record UpdateMyPhoneCommand(TenantId TenantId, string? Phone, string CorrelationId)
    : ICommand<MyMembershipDto>;

/// <summary>
/// The domain gives the error code, the validator gives the field (<c>errors.Phone</c>), the only thing the form can mark.
/// </summary>
public sealed class UpdateMyPhoneValidator : AbstractValidator<UpdateMyPhoneCommand>
{
    public UpdateMyPhoneValidator()
    {
        RuleFor(command => command.Phone)
            .Must(phone => MemberPhone.IsValid(phone!.Trim()))
            .When(command => !string.IsNullOrWhiteSpace(command.Phone))
            .WithMessage("The phone must be in E.164 format, for example +573001234567.");
    }
}

/// <summary>
/// Resolves the caller's own active membership. Session only, no permission: the number is personal data and the route only ever
/// reaches the caller's own row. Another tenant's route, or a membership that is not active, is a 403 and never a 404, so the
/// route does not reveal whether the caller belongs somewhere.
/// </summary>
internal static class MyMembershipLoader
{
    public static async Task<Membership> LoadAsync(
        IMembershipRepository memberships,
        IExecutionContext executionContext,
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        if (executionContext.TenantId == tenantId)
        {
            var membership = await memberships.FindByUserAndTenantAsync(
                executionContext.SubjectId, tenantId, cancellationToken);
            if (membership is { State: MembershipState.Active })
            {
                return membership;
            }
        }

        throw new RequestForbiddenException(
            "authorization.denied",
            "The subject has no active membership in this tenant.");
    }
}

public sealed class GetMyMembershipHandler(
    IMembershipRepository membershipRepository,
    IExecutionContext executionContext,
    MembershipPhoneReader phones)
    : IQueryHandler<GetMyMembershipQuery, MyMembershipDto>
{
    public async Task<MyMembershipDto> HandleAsync(
        GetMyMembershipQuery query,
        CancellationToken cancellationToken)
    {
        var membership = await MyMembershipLoader.LoadAsync(
            membershipRepository, executionContext, query.TenantId, cancellationToken);
        var phone = await phones.ForAsync(membership, cancellationToken);
        return new MyMembershipDto(membership.Id.Value, phone.Phone, phone.SourceName);
    }
}

/// <summary>
/// The member sets or clears their own phone. No <c>If-Match</c>: it is personal data, like marking a notification read. It does
/// raise the membership version (the aggregate counts it as a profile change), so a Members dialog opened before gets a 412 on its
/// next save, which is correct. The audit row names <c>phone</c> and never the number.
/// </summary>
public sealed class UpdateMyPhoneHandler(
    IMembershipRepository membershipRepository,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IClock clock,
    IValidator<UpdateMyPhoneCommand> validator,
    MembershipPhoneReader phones)
    : ICommandHandler<UpdateMyPhoneCommand, MyMembershipDto>
{
    public async Task<MyMembershipDto> HandleAsync(
        UpdateMyPhoneCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var membership = await MyMembershipLoader.LoadAsync(
            membershipRepository, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        if (membership.ChangeOwnPhone(command.Phone, now))
        {
            auditRecorder.Record(
                command.TenantId.Value,
                executionContext.SubjectId,
                "tenancy.membership.phone_updated",
                "membership",
                membership.Id.ToString(),
                "success",
                ["phone"],
                now);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var phone = await phones.ForAsync(membership, cancellationToken);
        return new MyMembershipDto(membership.Id.Value, phone.Phone, phone.SourceName);
    }
}
