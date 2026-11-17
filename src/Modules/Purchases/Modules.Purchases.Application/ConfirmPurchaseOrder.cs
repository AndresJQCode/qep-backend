using BuildingBlocks.Application;
using FluentValidation;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

public sealed record ConfirmPurchaseOrderCommand(
    Guid TenantId,
    Guid OrderId,
    long ExpectedVersion,
    IReadOnlyList<PurchaseOrderLineConfirmationInput> Lines)
    : ICommand<PurchaseOrderDetailDto>;

/// <summary>
/// D-9: cantidad confirmada ≥ 0 (0 rechaza la línea) y costo confirmado &gt; 0 siempre, también en la rechazada. El dominio no
/// mira signos, así que es acá o en ningún lado.
/// </summary>
public sealed class ConfirmPurchaseOrderValidator : AbstractValidator<ConfirmPurchaseOrderCommand>
{
    public ConfirmPurchaseOrderValidator()
    {
        RuleForEach(command => command.Lines).ChildRules(line =>
        {
            line.RuleFor(input => input.ConfirmedQuantity).GreaterThanOrEqualTo(0m);
            line.RuleFor(input => input.ConfirmedUnitCost).GreaterThan(0m);
        });
    }
}

/// <summary>
/// Registra lo que confirmó el proveedor. El cuerpo trae cada línea de la orden exactamente una vez: si falta una, se repite o
/// trae una ajena, 422 <c>confirm_lines_mismatch</c> antes de tocar nada (ni estado ni versión).
/// </summary>
public sealed class ConfirmPurchaseOrderHandler(
    IExecutionContext executionContext,
    IMembershipDirectory membershipDirectory,
    IClock clock,
    IPurchaseOrderRepository orders,
    IPurchasesUnitOfWork unitOfWork,
    IPurchasesAuditPublisher auditPublisher,
    PurchaseOrderComposer composer,
    IValidator<ConfirmPurchaseOrderCommand> validator)
    : ICommandHandler<ConfirmPurchaseOrderCommand, PurchaseOrderDetailDto>
{
    private static readonly string[] ConfirmedFields = ["status", "lines"];

    public async Task<PurchaseOrderDetailDto> HandleAsync(ConfirmPurchaseOrderCommand command, CancellationToken cancellationToken)
    {
        PurchasesAuthorization.EnsureAuthorized(executionContext, command.TenantId, PurchasesPermissions.OrderManage);
        var memberId = await PurchasesAuthorization.ResolveMemberAsync(
            executionContext, membershipDirectory, command.TenantId, cancellationToken);
        var order = await orders.GetAsync(command.TenantId, new PurchaseOrderId(command.OrderId), cancellationToken)
            ?? throw PurchasesConflicts.NotFound(command.OrderId);
        PurchasesConflicts.EnsureVersion(order, command.ExpectedVersion);
        PurchasesConflicts.EnsureAllowed(order, PurchaseOrderAction.Confirm);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var lines = command.Lines ?? [];
        var bodyIds = lines.Select(line => line.LineId).ToList();
        var matches = bodyIds.Count == order.Lines.Count
            && bodyIds.Distinct().Count() == bodyIds.Count
            && order.Lines.All(line => bodyIds.Contains(line.Id));
        if (!matches)
        {
            throw new PurchasesDomainException(
                PurchasesErrorCodes.ConfirmLinesMismatch, "The confirmation must bring every line of the order exactly once.");
        }

        var now = clock.UtcNow;
        order.Confirm(
            lines.Select(line => new PurchaseOrderLineConfirmation(line.LineId, line.ConfirmedQuantity, line.ConfirmedUnitCost)).ToList(),
            memberId,
            now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            PurchasesAuditActions.Confirmed,
            PurchasesAuditActions.Resources.PurchaseOrder,
            order.Id.ToString(),
            "success",
            ConfirmedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.DetailAsync(order, cancellationToken);
    }
}
