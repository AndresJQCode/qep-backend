using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record DeleteClientClassificationCommand(Guid TenantId, Guid ClassificationId)
    : ICommand<ClientClassificationDeletedResult>;

// BuildingBlocks no tiene un ICommand sin resultado. Mismo patron que TaxRateDeletedResult en
// Catalog: el endpoint responde 204 y no lo mira.
public sealed record ClientClassificationDeletedResult(bool Deleted);

/// <summary>
/// Borra una clasificacion de cliente **si nadie la usa**.
///
/// La condicion no la inventa este handler: <c>customers.customers.classification_id</c>
/// referencia <c>customers.client_classifications(tenant_id, id)</c> con <c>ON DELETE RESTRICT</c>
/// desde la Fase 3, asi que PostgreSQL ya la impone. Lo que agrega el handler es que el llamador
/// reciba un 422 que se entiende en vez de una violacion de foreign key convertida en 500 — mismo
/// patron que <c>DeleteTaxRateHandler</c> en Catalog.
///
/// Sin validador: el comando no lleva texto libre. Y sin regla de dominio: "estar en uso" no es
/// un invariante de <see cref="ClientClassification"/> —el agregado no sabe nada de clientes—
/// sino una pregunta sobre el catalogo, que se responde con una consulta.
/// </summary>
public sealed class DeleteClientClassificationHandler(
    IClientClassificationRepository repository,
    ICustomerRepository customerRepository,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeleteClientClassificationCommand, ClientClassificationDeletedResult>
{
    public async Task<ClientClassificationDeletedResult> HandleAsync(
        DeleteClientClassificationCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes que nada: quien tiene el permiso para otro tenant tiene que llevarse
        // un 403 antes de averiguar si el id existe aca.
        CustomersAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CustomersPermissions.ClassificationManage);

        var classificationId = new ClientClassificationId(command.ClassificationId);

        // FindAsync filtra por tenant, asi que una clasificacion ajena sale por aca como 404 y
        // nunca llega al DELETE.
        var classification = await repository.FindAsync(
            command.TenantId, classificationId, cancellationToken)
            ?? throw ClientClassificationNotFound.For(command.ClassificationId);

        if (await customerRepository.AnyWithClassificationAsync(
                command.TenantId, classificationId, cancellationToken))
        {
            throw new CustomersDomainException(
                "customers.classification.in_use",
                "The client classification cannot be deleted because at least one customer " +
                "uses it.");
        }

        var now = clock.UtcNow;
        repository.Remove(classification);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "customers.classification.deleted",
            classification.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ClientClassificationDeletedResult(true);
    }
}
