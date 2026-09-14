using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record ApproveOrderCommand(Guid TenantId, Guid QuotationId)
    : ICommand<OrderDto>;

/// <summary>
/// El visto bueno sobre un pedido ya registrado.
///
/// Existe separado de la conversión porque son dos personas: quien cotiza registra el pedido con
/// sus comprobantes, y quien controla los revisa y aprueba. Mientras esa revisión no ocurre el
/// pedido queda <c>Pending</c>, y ese estado es justamente lo que hace visible el paso.
///
/// Hoy exige el mismo permiso que registrar (<see cref="OrdersPermissions.OrderManage"/>): separar
/// los dos roles es una decisión de permisos que este slice no toma. Cuando exista el permiso
/// propio de aprobación, se cambia acá y en ningún otro lado.
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
            executionContext, command.TenantId, OrdersPermissions.OrderManage);

        var order = await repository.FindByQuotationIdAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw OrderNotFound.For(command.QuotationId);

        var approvedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        order.Approve(approvedBy, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.sale.approved",
            order.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return order.ToDto();
    }
}
