using BuildingBlocks.Application;
using Modules.Suppliers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Suppliers.Application;

public sealed record ActivateSupplierCommand(Guid TenantId, Guid SupplierId)
    : ICommand<SupplierResponse>;

// Sin validador: el comando no lleva texto libre. Activar uno ya activo lo rechaza el agregado
// (already_active). Activar es administrar: usa el permiso Manage.
public sealed class ActivateSupplierHandler(
    ISupplierRepository repository,
    ISuppliersUnitOfWork unitOfWork,
    ISuppliersAuditPublisher auditPublisher,
    ISupplierGeographyLookup geographyLookup,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ActivateSupplierCommand, SupplierResponse>
{
    public async Task<SupplierResponse> HandleAsync(
        ActivateSupplierCommand command,
        CancellationToken cancellationToken)
    {
        SuppliersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, SuppliersPermissions.Manage);

        var supplier = await repository.GetAsync(
            command.TenantId, new SupplierId(command.SupplierId), cancellationToken)
            ?? throw SupplierNotFound.For(command.SupplierId);

        var now = clock.UtcNow;
        supplier.Activate(now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            SupplierAuditActions.Activated,
            supplier.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await supplier.ToResponseAsync(geographyLookup, cancellationToken);
    }
}
