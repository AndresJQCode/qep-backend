using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record DeleteTaxRateCommand(Guid TenantId, Guid TaxRateId)
    : ICommand<TaxRateDeletedResult>;

// BuildingBlocks no tiene un ICommand sin resultado, y agregárselo sería tocar infraestructura
// compartida por seis módulos para ahorrar un record. Mismo patrón que SoftDeleteResult y
// CancelUploadResult en Storage: el endpoint responde 204 y no lo mira.
public sealed record TaxRateDeletedResult(bool Deleted);

/// <summary>
/// Borra una tasa de impuesto **si nadie la usa**.
///
/// La condición no la inventa este handler: `catalog.products.tax_rate_id` referencia
/// `catalog.tax_rates(id)` con `ON DELETE RESTRICT` desde `CAT-04`, así que PostgreSQL ya la
/// impone. Lo que agrega el handler es que el llamador reciba un **422 que se entiende** en vez
/// de una violación de foreign key convertida en 500.
///
/// Sin validador: el comando no lleva texto libre. Y sin regla de dominio: «estar en uso» no es
/// un invariante de <see cref="TaxRate"/> —el agregado no sabe nada de productos— sino una
/// pregunta sobre el catálogo, que se responde con una consulta.
/// </summary>
public sealed class DeleteTaxRateHandler(
    ITaxRateRepository repository,
    IProductRepository productRepository,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeleteTaxRateCommand, TaxRateDeletedResult>
{
    public async Task<TaxRateDeletedResult> HandleAsync(
        DeleteTaxRateCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes que nada, como en el resto del módulo: quien tiene el permiso para otro
        // tenant tiene que llevarse un 403 antes de averiguar si el id existe acá.
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.TaxRateManage);

        var taxRateId = new TaxRateId(command.TaxRateId);

        // FindAsync filtra por tenant, así que una tasa ajena sale por acá como 404 y nunca llega
        // al DELETE. La prueba que importa no es el status: es que la fila del otro tenant siga
        // existiendo después (CA-CAT-06-03).
        var taxRate = await repository.FindAsync(command.TenantId, taxRateId, cancellationToken)
            ?? throw TaxRateNotFound.For(command.TaxRateId);

        if (await productRepository.AnyWithTaxRateAsync(
                command.TenantId, taxRateId, cancellationToken))
        {
            throw new CatalogDomainException(
                "catalog.tax_rate.in_use",
                "The tax rate cannot be deleted because at least one product uses it.");
        }

        var now = clock.UtcNow;
        repository.Remove(taxRate);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.tax_rate.deleted",
            taxRate.Id.ToString(),
            "success",
            now);

        // Si entre la consulta de arriba y este commit alguien creó un producto que usa la tasa,
        // el RESTRICT lo frena acá y CatalogUnitOfWork lo traduce al mismo código. La ventana es
        // chica y existe; sin esa traducción, ese caso sale como 500 (CA-CAT-06-08).
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new TaxRateDeletedResult(true);
    }
}
