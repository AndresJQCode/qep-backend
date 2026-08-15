using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record DeactivateTaxRateCommand(Guid TenantId, Guid TaxRateId)
    : ICommand<TaxRateDto>;

// Sin validador: el comando no lleva texto libre. Desactivar dos veces lo rechaza el agregado,
// que es donde va esa regla.
public sealed class DeactivateTaxRateHandler(
    ITaxRateRepository repository,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeactivateTaxRateCommand, TaxRateDto>
{
    public async Task<TaxRateDto> HandleAsync(
        DeactivateTaxRateCommand command,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.TaxRateManage);

        var taxRate = await repository.FindAsync(
            command.TenantId, new TaxRateId(command.TaxRateId), cancellationToken)
            ?? throw TaxRateNotFound.For(command.TaxRateId);

        var now = clock.UtcNow;
        taxRate.Deactivate(now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.tax_rate.deactivated",
            taxRate.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return taxRate.ToDto();
    }
}
