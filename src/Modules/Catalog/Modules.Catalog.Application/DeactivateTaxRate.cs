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
    IProductRepository productRepository,
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

        var taxRateId = new TaxRateId(command.TaxRateId);
        var taxRate = await repository.FindAsync(command.TenantId, taxRateId, cancellationToken)
            ?? throw TaxRateNotFound.For(command.TaxRateId);

        // Mismo código de dominio que `DeleteTaxRateHandler` — "en uso" es la misma razón en los
        // dos casos, y el llamador ya la distingue por la acción que pidió, no por el código.
        // A diferencia de borrar, acá no hay ninguna FK que lo frene solo: nada impide guardar
        // `IsActive = false` en una tasa que un producto sigue usando, así que sin este chequeo
        // el 422 nunca llegaría y la tasa quedaría inactiva mientras un producto activo la
        // sigue resolviendo como si nada.
        //
        // `taxRate.IsActive` se revisa primero para no gastar la consulta y, sobre todo, para no
        // taparle el código a `Deactivate()`: una tasa que ya está inactiva tiene que seguir
        // devolviendo `already_inactive` aunque además esté en uso — eso es lo que de verdad pasó,
        // y `in_use` ahí confundiría a alguien que ni siquiera está pidiendo la primera
        // desactivación.
        if (taxRate.IsActive &&
            await productRepository.AnyWithTaxRateAsync(
                command.TenantId, taxRateId, cancellationToken))
        {
            throw new CatalogDomainException(
                "catalog.tax_rate.in_use",
                "The tax rate cannot be deactivated because at least one product uses it.");
        }

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
