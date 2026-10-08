using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

public sealed record VoidPosSaleCommand(Guid TenantId, Guid SaleId, string? Reason) : ICommand<PosSaleResponse>;

public sealed class VoidPosSaleValidator : AbstractValidator<VoidPosSaleCommand>
{
    public VoidPosSaleValidator()
    {
        RuleFor(command => command.Reason)
            .Must(reason => reason is not null
                && reason.Trim().Length is >= PosLimits.VoidReasonMinLength and <= PosLimits.VoidReasonMaxLength)
            .WithMessage("The void reason must have between 3 and 500 characters.");
    }
}

/// <summary>
/// Anula quien tenga pos.sale.void (hoy sólo admin), no hace falta ser el cajero. Sólo con la caja
/// de la venta abierta: una caja cerrada es un arqueo que alguien ya firmó.
/// </summary>
public sealed class VoidPosSaleHandler(
    IPosSaleRepository sales,
    ICashSessionRepository sessions,
    IPosUnitOfWork unitOfWork,
    IPosAuditPublisher auditPublisher,
    IPosCashierLookup cashiers,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    ITenantClock tenantClock,
    IValidator<VoidPosSaleCommand> validator)
    : ICommandHandler<VoidPosSaleCommand, PosSaleResponse>
{
    public async Task<PosSaleResponse> HandleAsync(VoidPosSaleCommand command, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, command.TenantId, PosPermissions.SaleVoid);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var member = await PosCashierResolver.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var sale = await sales.FindAsync(command.TenantId, new PosSaleId(command.SaleId), cancellationToken)
            ?? throw PosNotFound.Sale(command.SaleId);
        var session = await sessions.FindAsync(command.TenantId, sale.CashSessionId, cancellationToken)
            ?? throw new InvalidOperationException($"Sale '{sale.Id}' points to a missing cash session.");

        var now = clock.UtcNow;
        sale.Void(command.Reason!, member, now);   // already_voided primero (paso 3)
        session.RegisterVoid(sale, now);           // void_session_closed después (paso 4)
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, "pos.sale.voided", "pos_sale",
            sale.Id.ToString(), "success", [], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await PosSaleResponses.BuildAsync(sale, sessions, cashiers, tenantClock, cancellationToken);
    }
}
