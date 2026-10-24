using BuildingBlocks.Application;
using Modules.Suppliers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Suppliers.Application;

public sealed record DeactivateSupplierCommand(Guid TenantId, Guid SupplierId)
    : ICommand<SupplierResponse>;

// Sin validador, por la misma razón que ActivateSupplier. Desactivar uno ya inactivo lo rechaza el
// agregado (already_inactive).
public sealed class DeactivateSupplierHandler(
    ISupplierRepository repository,
    ISuppliersUnitOfWork unitOfWork,
    ISuppliersAuditPublisher auditPublisher,
    ISupplierGeographyLookup geographyLookup,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeactivateSupplierCommand, SupplierResponse>
{
    public async Task<SupplierResponse> HandleAsync(
        DeactivateSupplierCommand command,
        CancellationToken cancellationToken)
    {
        SuppliersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, SuppliersPermissions.Manage);

        var supplier = await repository.GetAsync(
            command.TenantId, new SupplierId(command.SupplierId), cancellationToken)
            ?? throw SupplierNotFound.For(command.SupplierId);

        var now = clock.UtcNow;
        supplier.Deactivate(now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            SupplierAuditActions.Deactivated,
            supplier.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await supplier.ToResponseAsync(geographyLookup, cancellationToken);
    }
}
