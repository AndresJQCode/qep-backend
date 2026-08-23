using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record DeactivateCustomerCommand(Guid TenantId, Guid CustomerId)
    : ICommand<CustomerDto>;

public sealed record ActivateCustomerCommand(Guid TenantId, Guid CustomerId)
    : ICommand<CustomerDto>;

// Sin validador: ninguno de los dos comandos lleva texto libre. Inactivar dos veces lo rechaza el
// agregado, que es donde va esa regla.
public sealed class DeactivateCustomerHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeactivateCustomerCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        DeactivateCustomerCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de leer el repositorio, no despues: consultar primero le confirma al
        // llamador que el id existe. La revision de CAT-02 ya corrigio ese orden una vez.
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.CustomerManage);

        var customer = await repository.FindAsync(
            command.TenantId, new CustomerId(command.CustomerId), cancellationToken)
            ?? throw CustomerNotFound.For(command.CustomerId);

        var now = clock.UtcNow;
        customer.Deactivate(now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.customer.deactivated",
            customer.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await customer.ToDtoAsync(geographyLookup, classificationRepository, cancellationToken);
    }
}

/// <summary>
/// La vuelta de la inactivacion.
///
/// `CLI-01` no lista este verbo. Existe porque sin el un cliente inactivo es terminal —
/// <c>Customer.Update</c> abre con <c>EnsureActive</c> y nada devuelve <c>IsActive</c> a true—, que
/// es la falta que `CAT-07` tuvo que corregir en producto y que `EMP-08` ya nacio cubriendo.
///
/// No estrena permiso: reactivar es administrar, y un permiso publicado antes que su
/// funcionalidad le dice al frontend que existe algo que no existe.
/// </summary>
public sealed class ActivateCustomerHandler(
    ICustomerRepository repository,
    IClientClassificationRepository classificationRepository,
    ICustomerGeographyLookup geographyLookup,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ActivateCustomerCommand, CustomerDto>
{
    public async Task<CustomerDto> HandleAsync(
        ActivateCustomerCommand command,
        CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.CustomerManage);

        var customer = await repository.FindAsync(
            command.TenantId, new CustomerId(command.CustomerId), cancellationToken)
            ?? throw CustomerNotFound.For(command.CustomerId);

        var now = clock.UtcNow;
        customer.Activate(now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.customer.activated",
            customer.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await customer.ToDtoAsync(geographyLookup, classificationRepository, cancellationToken);
    }
}
