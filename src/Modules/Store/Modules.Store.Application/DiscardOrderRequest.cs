using BuildingBlocks.Application;
using FluentValidation;
using Modules.Store.Domain;
using Modules.Tenancy.Application;

namespace Modules.Store.Application;

/// <summary>El cuerpo de <c>POST /discard</c>.</summary>
public sealed record DiscardOrderRequestBody(string? Reason, string? Note);

/// <param name="Reason"><c>Spam</c>, <c>Duplicate</c> u <c>Other</c>; <c>Other</c> exige nota.</param>
public sealed record DiscardOrderRequestCommand(Guid TenantId, Guid RequestId, string? Reason, string? Note)
    : ICommand<StoreOrderRequestRowDto>;

public sealed class DiscardOrderRequestValidator : AbstractValidator<DiscardOrderRequestCommand>
{
    public DiscardOrderRequestValidator()
    {
        RuleFor(command => command.Reason)
            .NotEmpty()
            .Must(reason => reason is null || Enum.TryParse<StoreDiscardReason>(reason, ignoreCase: true, out _))
            .WithMessage("El motivo debe ser Spam, Duplicate u Other.");
        RuleFor(command => command.Note).MaximumLength(StoreOrderRequest.DiscardNoteMaxLength);
    }
}

public sealed class DiscardOrderRequestHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IStoreMemberDirectory members,
    IStoreOrderRequestRepository requests,
    IStoreUnitOfWork unitOfWork,
    IStoreAuditPublisher auditPublisher,
    ITenantClock tenantClock,
    OrderRequestRowComposer composer,
    IValidator<DiscardOrderRequestCommand> validator)
    : ICommandHandler<DiscardOrderRequestCommand, StoreOrderRequestRowDto>
{
    public async Task<StoreOrderRequestRowDto> HandleAsync(DiscardOrderRequestCommand command, CancellationToken cancellationToken)
    {
        await StoreAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, StorePermissions.OrderManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var caller = await members.ResolveCallerAsync(command.TenantId, cancellationToken);
        var request = await requests.FindAsync(command.TenantId, command.RequestId, cancellationToken)
            ?? throw StoreNotFound.OrderRequest(command.RequestId);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;

        try
        {
            request.Discard(
                caller.MembershipId, Enum.Parse<StoreDiscardReason>(command.Reason!, ignoreCase: true), command.Note, now);
        }
        catch (StoreDomainException exception) when (StoreConversionErrors.IsStateConflict(exception))
        {
            throw StoreConversionErrors.ToConflict(exception);
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            StoreAuditActions.RequestDiscarded,
            StoreAuditActions.Resources.OrderRequest,
            request.Id.ToString(),
            "success",
            ["status", "discardReason"],
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (await composer.ComposeAsync(command.TenantId, [request], now, cancellationToken))[0];
    }
}

/// <summary>
/// El dominio dice «ya no es nueva» y «alguien la está tomando» con <see cref="StoreDomainException"/> (422); para la bandeja son
/// conflictos de estado (409): la interfaz relee y sigue.
/// </summary>
internal static class StoreConversionErrors
{
    public static bool IsStateConflict(StoreDomainException exception) =>
        exception.Code is StoreErrorCodes.NotNew or StoreErrorCodes.BeingTaken;

    public static ResourceConflictException ToConflict(StoreDomainException exception) =>
        new(exception.Code, exception.Message, exception);
}
