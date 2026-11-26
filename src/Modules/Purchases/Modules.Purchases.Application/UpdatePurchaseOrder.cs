using BuildingBlocks.Application;
using FluentValidation;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

public sealed record UpdatePurchaseOrderCommand(
    Guid TenantId,
    Guid OrderId,
    long ExpectedVersion,
    Guid SupplierId,
    string Currency,
    string? Notes,
    IReadOnlyList<PurchaseOrderLineInput> Lines)
    : ICommand<PurchaseOrderDetailDto>, IPurchaseOrderWriteCommand;

/// <summary>
/// Reemplaza proveedor, moneda, notas y líneas de un borrador (D-7): las líneas vuelven a tomar código, nombre y tasa actuales
/// del catálogo y salen con ids nuevos. Orden: 403 → 404 → 412 → 409 → 422.
/// </summary>
public sealed class UpdatePurchaseOrderHandler(
    IExecutionContext executionContext,
    IMembershipDirectory membershipDirectory,
    IClock clock,
    PurchaseOrderLineBuilder lineBuilder,
    IPurchaseOrderRepository orders,
    IPurchasesUnitOfWork unitOfWork,
    IPurchasesAuditPublisher auditPublisher,
    PurchaseOrderComposer composer,
    IValidator<UpdatePurchaseOrderCommand> validator)
    : ICommandHandler<UpdatePurchaseOrderCommand, PurchaseOrderDetailDto>
{
    public async Task<PurchaseOrderDetailDto> HandleAsync(UpdatePurchaseOrderCommand command, CancellationToken cancellationToken)
    {
        PurchasesAuthorization.EnsureAuthorized(executionContext, command.TenantId, PurchasesPermissions.OrderManage);
        await PurchasesAuthorization.ResolveMemberAsync(executionContext, membershipDirectory, command.TenantId, cancellationToken);
        var order = await orders.GetAsync(command.TenantId, new PurchaseOrderId(command.OrderId), cancellationToken)
            ?? throw PurchasesConflicts.NotFound(command.OrderId);
        PurchasesConflicts.EnsureVersion(order, command.ExpectedVersion);
        PurchasesConflicts.EnsureAllowed(order, PurchaseOrderAction.Edit);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var build = await lineBuilder.BuildAsync(command.TenantId, command, cancellationToken);

        // D-10: editar vuelve a fijar el modo con el proveedor de hoy; un borrador de fase 2 a un vinculado pasa a vinculado acá.
        var now = clock.UtcNow;
        order.Replace(command.SupplierId, build.SupplierTenantId, build.Currency, command.Notes, build.Lines, now, build.ShowPrices);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            PurchasesAuditActions.Updated,
            PurchasesAuditActions.Resources.PurchaseOrder,
            order.Id.ToString(),
            "success",
            CreatePurchaseOrderHandler.WrittenFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.DetailAsync(order, cancellationToken);
    }
}
