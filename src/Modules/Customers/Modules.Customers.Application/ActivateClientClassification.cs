using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record ActivateClientClassificationCommand(Guid TenantId, Guid ClassificationId)
    : ICommand<ClientClassificationDto>;

// Sin validador, por la misma razon que DeactivateClientClassification: el comando no lleva
// texto libre. Activar algo ya activo lo rechaza el agregado, que es donde vive esa regla.
public sealed class ActivateClientClassificationHandler(
    IClientClassificationRepository repository,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ActivateClientClassificationCommand, ClientClassificationDto>
{
    public async Task<ClientClassificationDto> HandleAsync(
        ActivateClientClassificationCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de leer el repositorio, no despues: consultar primero le confirma al
        // llamador que el id existe.
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.ClassificationManage);

        var classification = await repository.FindAsync(
            command.TenantId, new ClientClassificationId(command.ClassificationId), cancellationToken)
            ?? throw ClientClassificationNotFound.For(command.ClassificationId);

        var now = clock.UtcNow;
        classification.Activate(now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.classification.activated",
            classification.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return classification.ToDto();
    }
}
