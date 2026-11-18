using BuildingBlocks.Application;
using FluentValidation;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

public sealed record CreatePurchaseOrderCommand(
    Guid TenantId,
    Guid SupplierId,
    string Currency,
    string? Notes,
    IReadOnlyList<PurchaseOrderLineInput> Lines)
    : ICommand<PurchaseOrderDetailDto>, IPurchaseOrderWriteCommand;

/// <summary>
/// Crea la orden en <c>Draft</c>. Todo lo que puede responder 422 (validación, moneda, proveedor, líneas) va antes de abrir la
/// transacción y pedir el número: un alta rechazada nunca gasta un consecutivo.
/// </summary>
public sealed class CreatePurchaseOrderHandler(
    IExecutionContext executionContext,
    IMembershipDirectory membershipDirectory,
    IClock clock,
    PurchaseOrderLineBuilder lineBuilder,
    IPurchaseOrderRepository orders,
    IPurchaseOrderNumberGenerator numbers,
    IPurchasesUnitOfWork unitOfWork,
    IPurchasesAuditPublisher auditPublisher,
    PurchaseOrderComposer composer,
    IValidator<CreatePurchaseOrderCommand> validator)
    : ICommandHandler<CreatePurchaseOrderCommand, PurchaseOrderDetailDto>
{
    internal static readonly string[] WrittenFields = ["supplierId", "currency", "notes", "lines"];

    public async Task<PurchaseOrderDetailDto> HandleAsync(CreatePurchaseOrderCommand command, CancellationToken cancellationToken)
    {
        PurchasesAuthorization.EnsureAuthorized(executionContext, command.TenantId, PurchasesPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var memberId = await PurchasesAuthorization.ResolveMemberAsync(
            executionContext, membershipDirectory, command.TenantId, cancellationToken);
        var lines = await lineBuilder.BuildAsync(command.TenantId, command, cancellationToken);

        PurchaseOrder order;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var sequence = await numbers.NextAsync(command.TenantId, cancellationToken);
            var now = clock.UtcNow;
            order = PurchaseOrder.Create(
                command.TenantId,
                $"OC-{sequence:D4}",
                command.SupplierId,
                command.Currency,
                command.Notes,
                lines,
                memberId,
                now);
            orders.Add(order);
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                PurchasesAuditActions.Created,
                PurchasesAuditActions.Resources.PurchaseOrder,
                order.Id.ToString(),
                "success",
                WrittenFields,
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await composer.DetailAsync(order, cancellationToken);
    }
}
