using BuildingBlocks.Application;
using FluentValidation;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <summary>Cuerpo de <c>POST /charges/{id}/pay</c> y <c>PUT /charges/{id}/payment</c>; la fecha es nullable para que su ausencia sea <c>validation.failed</c> con el campo y no un 400 del binding.</summary>
public sealed record PayChargeRequest(DateOnly? PaidOn, string? Note);

public interface IPaymentInput
{
    DateOnly PaidOn { get; }

    string? Note { get; }
}

public sealed record PayChargeCommand(Guid TenantId, Guid ChargeId, DateOnly PaidOn, string? Note)
    : ICommand<ChargeDto>, IPaymentInput
{
    public static PayChargeCommand From(Guid tenantId, Guid chargeId, PayChargeRequest request) =>
        new(tenantId, chargeId, request.PaidOn ?? default, request.Note);
}

internal static class PaymentRules
{
    /// <summary>La fecha es obligatoria y la nota, ya recortada, no pasa de 1000 caracteres; la fecha futura la juzga el dominio con la «hoy» del tenant.</summary>
    public static void Apply<T>(AbstractValidator<T> validator)
        where T : IPaymentInput
    {
        validator.RuleFor(command => command.PaidOn).NotEqual(default(DateOnly));
        validator.RuleFor(command => command.Note)
            .Must(note => note is null || note.Trim().Length <= Charge.PaymentNoteMaxLength)
            .WithMessage($"The note cannot exceed {Charge.PaymentNoteMaxLength} characters.");
    }
}

public sealed class PayChargeValidator : AbstractValidator<PayChargeCommand>
{
    public PayChargeValidator() => PaymentRules.Apply(this);
}

/// <summary>
/// Marca un cobro como pagado (spec §3.2): autorización → cobro del tenant → «hoy» del tenant → <c>MarkPaid</c> → auditoría → guardar.
/// No genera nada: los cobros nuevos son del worker. Quien marca es su membresía activa; sin ella no hay a quién atribuirlo (403).
/// </summary>
public sealed class PayChargeHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IMembershipDirectory membershipDirectory,
    IChargeRepository charges,
    IServiceContractRepository services,
    IServiceContractsUnitOfWork unitOfWork,
    IServiceContractsAuditPublisher auditPublisher,
    IClock clock,
    ChargeDtoComposer composer,
    IValidator<PayChargeCommand> validator)
    : ICommandHandler<PayChargeCommand, ChargeDto>
{
    private static readonly string[] PaidFields = ["paidOn", "note"];

    public async Task<ChargeDto> HandleAsync(PayChargeCommand command, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ServiceContractsPermissions.Manage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var charge = await charges.FindAsync(command.TenantId, command.ChargeId, cancellationToken)
            ?? throw ServiceContractsNotFound.Charge(command.ChargeId);
        var paidBy = await membershipDirectory.FindActiveMembershipIdAsync(
                executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw ServiceContractsAuthorization.Denied();

        var today = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).Today;
        var now = clock.UtcNow;
        charge.MarkPaid(command.PaidOn, command.Note, paidBy, today, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            ServiceContractsAuditActions.ChargePaid,
            ServiceContractsAuditActions.Resources.Charge,
            charge.Id.ToString(),
            "success",
            PaidFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await ChargeComposition.ComposeOneAsync(command.TenantId, charge, services, composer, today, cancellationToken);
    }
}
