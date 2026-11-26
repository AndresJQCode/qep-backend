using BuildingBlocks.Application;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

/// <summary>
/// Los tres cambios de estado sin cuerpo: <c>Send</c>, <c>Receive</c> y <c>Cancel</c>. Confirmar trae líneas y va aparte
/// (<see cref="ConfirmPurchaseOrderCommand"/>).
/// </summary>
public sealed record TransitionPurchaseOrderCommand(
    Guid TenantId,
    Guid OrderId,
    long ExpectedVersion,
    PurchaseOrderAction Action)
    : ICommand<PurchaseOrderDetailDto>;

/// <summary>
/// Un handler para enviar, recibir y cancelar. Enviar vuelve a mirar el proveedor: si lo desactivaron (o ya no está) después del
/// alta, 422 <c>supplier_inactive</c> y la orden sigue en borrador. Recibir y cancelar no tienen reglas extra.
/// <para>Mayorista fase 3: una orden vinculada (el modo fijado al guardar, D-10) se envía creando el pedido en el mayorista
/// (<see cref="IWholesaleOrderIntake"/>, idempotente, D-8) y adopta sus líneas; cancelarla en <c>Sent</c> anula primero el
/// pedido (D-12): si el mayorista ya lo aprobó, el 409 sale de ahí y la orden no cambia. Cancelar un borrador vinculado anula
/// el pedido que haya dejado un envío que no terminó de guardarse (revisión del bloque B, M1).</para>
/// </summary>
public sealed class TransitionPurchaseOrderHandler(
    IExecutionContext executionContext,
    IMembershipDirectory membershipDirectory,
    IClock clock,
    IPurchasesSupplierLookup suppliers,
    IPurchaseOrderRepository orders,
    IPurchasesUnitOfWork unitOfWork,
    IPurchasesAuditPublisher auditPublisher,
    PurchaseOrderComposer composer,
    IWholesaleOrderIntake wholesaleOrders,
    PurchaseOrderEmailDelivery emailDelivery)
    : ICommandHandler<TransitionPurchaseOrderCommand, PurchaseOrderDetailDto>
{
    private static readonly string[] StatusField = ["status"];

    public async Task<PurchaseOrderDetailDto> HandleAsync(TransitionPurchaseOrderCommand command, CancellationToken cancellationToken)
    {
        var auditAction = command.Action switch
        {
            PurchaseOrderAction.Send => PurchasesAuditActions.Sent,
            PurchaseOrderAction.Receive => PurchasesAuditActions.Received,
            PurchaseOrderAction.Cancel => PurchasesAuditActions.Cancelled,
            _ => throw new ArgumentOutOfRangeException(
                nameof(command), command.Action, "Only send, receive and cancel go through this handler."),
        };

        PurchasesAuthorization.EnsureAuthorized(executionContext, command.TenantId, PurchasesPermissions.OrderManage);
        var memberId = await PurchasesAuthorization.ResolveMemberAsync(
            executionContext, membershipDirectory, command.TenantId, cancellationToken);
        var order = await orders.GetAsync(command.TenantId, new PurchaseOrderId(command.OrderId), cancellationToken)
            ?? throw PurchasesConflicts.NotFound(command.OrderId);
        PurchasesConflicts.EnsureVersion(order, command.ExpectedVersion);
        PurchasesConflicts.EnsureAllowed(order, command.Action);

        PurchasesSupplierRef? supplier = null;
        if (command.Action == PurchaseOrderAction.Send)
        {
            supplier = await suppliers.FindAsync(command.TenantId, order.SupplierId, cancellationToken);
            if (supplier is not { IsActive: true })
            {
                throw PurchaseOrderLineBuilder.SupplierInactive();
            }
        }

        switch (command.Action)
        {
            case PurchaseOrderAction.Send when order.IsLinked:
                var submitted = await wholesaleOrders.SubmitAsync(
                    new WholesaleIntakeRequest(
                        command.TenantId,
                        order.SupplierTenantId!.Value,
                        order.Id.Value,
                        order.Number,
                        order.Notes,
                        order.Lines
                            .Select(line => new WholesaleIntakeLine(line.Id, line.SupplierVariantId!.Value, line.Quantity!.Value))
                            .ToList(),
                        order.Currency),
                    cancellationToken);
                order.SendToWholesaler(
                    submitted.OrderId, submitted.OrderNumber, submitted.Lines, memberId, clock.UtcNow, submitted.ShowPrices);
                break;
            case PurchaseOrderAction.Send:
                order.Send(memberId, clock.UtcNow);
                break;
            case PurchaseOrderAction.Receive:
                order.Receive(memberId, clock.UtcNow);
                break;
            default:
                // D-12 en Sent; en Draft (revisión del bloque B, M1) el envío pudo dejar un pedido sin que la orden lo supiera.
                if (order is { IsLinked: true, Status: PurchaseOrderStatus.Sent or PurchaseOrderStatus.Draft })
                {
                    await wholesaleOrders.CancelAsync(
                        command.TenantId, order.SupplierTenantId!.Value, order.Id.Value, order.Number, order.LinkedOrderId,
                        cancellationToken);
                }

                order.Cancel(memberId, clock.UtcNow);
                break;
        }

        var now = clock.UtcNow;

        // Sin correo del proveedor no hay intento: el «sin correo» se conoce antes de guardar y queda en el mismo guardado.
        var sendsEmail = supplier is { Email: { } address } && !string.IsNullOrWhiteSpace(address);
        if (command.Action == PurchaseOrderAction.Send && !sendsEmail)
        {
            order.RecordEmail(PurchaseOrderEmailStatus.NoRecipient, null, now);
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            auditAction,
            PurchasesAuditActions.Resources.PurchaseOrder,
            order.Id.ToString(),
            "success",
            StatusField,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var detail = await composer.DetailAsync(order, cancellationToken);
        if (!sendsEmail)
        {
            return detail;
        }

        // La orden ya está enviada y guardada: el correo va después y su fallo no la deshace.
        var email = await emailDelivery.DeliverAsync(order, detail, supplier!, executionContext.SubjectId, clock.UtcNow, cancellationToken);
        return detail with { Email = email };
    }
}
