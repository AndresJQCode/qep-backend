using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record ApproveSaleCommand(Guid TenantId, Guid QuotationId)
    : ICommand<SaleDto>;

/// <summary>
/// El visto bueno sobre una venta ya registrada.
///
/// Existe separado de la conversión porque son dos personas: quien cotiza registra la venta con
/// sus comprobantes, y quien controla los revisa y aprueba. Mientras esa revisión no ocurre la
/// venta queda <c>Pending</c>, y ese estado es justamente lo que hace visible el paso.
///
/// Hoy exige el mismo permiso que registrar (<see cref="SalesPermissions.SaleManage"/>): separar
/// los dos roles es una decisión de permisos que este slice no toma. Cuando exista el permiso
/// propio de aprobación, se cambia acá y en ningún otro lado.
/// </summary>
public sealed class ApproveSaleHandler(
    ISaleRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ApproveSaleCommand, SaleDto>
{
    public async Task<SaleDto> HandleAsync(
        ApproveSaleCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, SalesPermissions.SaleManage);

        var sale = await repository.FindByQuotationIdAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw SaleNotFound.For(command.QuotationId);

        var approvedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        sale.Approve(approvedBy, now);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.sale.approved",
            sale.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return sale.ToDto();
    }
}
