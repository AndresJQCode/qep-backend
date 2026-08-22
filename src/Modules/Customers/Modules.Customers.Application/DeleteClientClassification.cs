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
/// Borra una clasificacion de cliente.
///
/// A diferencia de <c>DeleteTaxRateHandler</c> en Catalog, este handler **no** chequea si algo
/// la usa antes de borrar: hoy nada referencia <see cref="ClientClassification"/> por FK — ni
/// siquiera <c>Customer</c>, que tiene su propio enum <c>CustomerClassification</c> sin relacion
/// con este catalogo. El dia que algo referencie esta tabla, este handler necesita el mismo
/// guard que <c>DeleteTaxRateHandler</c> (una consulta al repositorio del que referencia, y un
/// 422 <c>customers.classification.in_use</c> antes de intentar el borrado).
///
/// Sin validador: el comando no lleva texto libre. Y sin regla de dominio: "estar en uso" no es
/// un invariante de <see cref="ClientClassification"/> hoy, no existe nada que preguntarle.
/// </summary>
public sealed class DeleteClientClassificationHandler(
    IClientClassificationRepository repository,
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
