using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record ApproveOrderCommand(Guid TenantId, Guid OrderId)
    : ICommand<OrderDto>;

/// <summary>
/// El visto bueno sobre un pedido ya registrado.
///
/// Existe separado de la conversión porque son dos personas: quien cotiza registra el pedido con
/// sus comprobantes, y quien controla los revisa y aprueba. Mientras esa revisión no ocurre el
/// pedido queda <c>Pending</c>, y ese estado es justamente lo que hace visible el paso.
///
/// Por eso exige su propio permiso (<see cref="OrdersPermissions.OrderApprove"/>) y no el de
/// registrar (<see cref="OrdersPermissions.OrderManage"/>): la asesora necesita gestionar pedidos
/// para convertir y editar, pero no puede darse el visto bueno a sí misma. De fábrica lo tienen
/// admin y facturación.
/// </summary>
public sealed class ApproveOrderHandler(
    IOrderRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ApproveOrderCommand, OrderDto>
{
    public async Task<OrderDto> HandleAsync(
        ApproveOrderCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderApprove);

        // Por el id del pedido: quien aprueba llega desde el listado de pedidos, no desde la
        // cotización. Un id que no es de este tenant es el mismo "no encontrado" de GET /orders/{id}.
        var order = await repository.FindByIdAsync(
            command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);

        var approvedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        order.Approve(approvedBy, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.order.approved",
            order.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return order.ToDto();
    }
}
