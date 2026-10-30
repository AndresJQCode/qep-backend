using BuildingBlocks.Application;
using FluentValidation;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

public sealed record UpdateChargePaymentCommand(Guid TenantId, Guid ChargeId, DateOnly PaidOn, string? Note)
    : ICommand<ChargeDto>, IPaymentInput
{
    public static UpdateChargePaymentCommand From(Guid tenantId, Guid chargeId, PayChargeRequest request) =>
        new(tenantId, chargeId, request.PaidOn ?? default, request.Note);
}

public sealed class UpdateChargePaymentValidator : AbstractValidator<UpdateChargePaymentCommand>
{
    public UpdateChargePaymentValidator() => PaymentRules.Apply(this);
}

/// <summary>Corrige la fecha y la nota de un cobro ya pagado (§3.2b); quién lo registró, cuándo, el monto y el vencimiento no cambian.</summary>
public sealed class UpdateChargePaymentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IChargeRepository charges,
    IServiceContractRepository services,
    IServiceContractsUnitOfWork unitOfWork,
    IServiceContractsAuditPublisher auditPublisher,
    IClock clock,
    ChargeDtoComposer composer,
    IValidator<UpdateChargePaymentCommand> validator)
    : ICommandHandler<UpdateChargePaymentCommand, ChargeDto>
{
    private static readonly string[] PaidFields = ["paidOn", "note"];

    public async Task<ChargeDto> HandleAsync(UpdateChargePaymentCommand command, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ServiceContractsPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var charge = await charges.FindAsync(command.TenantId, command.ChargeId, cancellationToken)
            ?? throw ServiceContractsNotFound.Charge(command.ChargeId);

        var today = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).Today;
        var now = clock.UtcNow;
        charge.UpdatePayment(command.PaidOn, command.Note, today, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            ServiceContractsAuditActions.ChargePaymentUpdated,
            ServiceContractsAuditActions.Resources.Charge,
            charge.Id.ToString(),
            "success",
            PaidFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await ChargeComposition.ComposeOneAsync(command.TenantId, charge, services, composer, today, cancellationToken);
    }
}
