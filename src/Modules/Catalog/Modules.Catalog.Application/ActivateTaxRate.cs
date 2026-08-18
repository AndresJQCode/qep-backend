using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record ActivateTaxRateCommand(Guid TenantId, Guid TaxRateId)
    : ICommand<TaxRateDto>;

// Sin validador, por la misma razón que DeactivateTaxRate: el comando no lleva texto libre.
// Activar algo ya activo lo rechaza el agregado, que es donde vive esa regla.
public sealed class ActivateTaxRateHandler(
    ITaxRateRepository repository,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ActivateTaxRateCommand, TaxRateDto>
{
    public async Task<TaxRateDto> HandleAsync(
        ActivateTaxRateCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de leer el repositorio, no después: consultar primero le confirma al
        // llamador que el id existe. La revisión de CAT-02 ya corrigió ese orden una vez.
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.TaxRateManage);

        var taxRate = await repository.FindAsync(
            command.TenantId, new TaxRateId(command.TaxRateId), cancellationToken)
            ?? throw TaxRateNotFound.For(command.TaxRateId);

        var now = clock.UtcNow;
        taxRate.Activate(now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.tax_rate.activated",
            taxRate.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return taxRate.ToDto();
    }
}
