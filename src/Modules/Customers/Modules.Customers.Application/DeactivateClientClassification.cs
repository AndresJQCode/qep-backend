using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record DeactivateClientClassificationCommand(Guid TenantId, Guid ClassificationId)
    : ICommand<ClientClassificationDto>;

// Sin validador: el comando no lleva texto libre. Desactivar dos veces lo rechaza el agregado,
// que es donde va esa regla.
public sealed class DeactivateClientClassificationHandler(
    IClientClassificationRepository repository,
    ICustomerRepository customerRepository,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeactivateClientClassificationCommand, ClientClassificationDto>
{
    public async Task<ClientClassificationDto> HandleAsync(
        DeactivateClientClassificationCommand command,
        CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.ClassificationManage);

        var classificationId = new ClientClassificationId(command.ClassificationId);
        var classification = await repository.FindAsync(
            command.TenantId, classificationId, cancellationToken)
            ?? throw ClientClassificationNotFound.For(command.ClassificationId);

        // Regla de negocio confirmada: misma restriccion que Delete. Una clasificacion en uso no
        // se inactiva hasta que se le reasignen sus clientes a otra.
        if (await customerRepository.AnyWithClassificationAsync(
                command.TenantId, classificationId, cancellationToken))
        {
            throw new CustomersDomainException(
                "customers.classification.in_use",
                "The client classification cannot be deactivated because at least one customer " +
                "uses it.");
        }

        var now = clock.UtcNow;
        classification.Deactivate(now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.classification.deactivated",
            classification.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return classification.ToDto();
    }
}
