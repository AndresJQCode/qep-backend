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

        var classification = await repository.FindAsync(
            command.TenantId, new ClientClassificationId(command.ClassificationId), cancellationToken)
            ?? throw ClientClassificationNotFound.For(command.ClassificationId);

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
