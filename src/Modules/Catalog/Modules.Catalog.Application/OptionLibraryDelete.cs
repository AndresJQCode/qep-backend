using BuildingBlocks.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record DeleteOptionCommand(Guid TenantId, Guid OptionId)
    : ICommand<OptionDeletedResult>;

// Mismo patrón que TaxRateDeletedResult: el endpoint responde 204 y no lo mira.
public sealed record OptionDeletedResult(bool Deleted);

/// <summary>
/// Borra una opción **si ninguna variante la usa**, ni a ella ni a ninguno de sus valores (spec
/// §3.1). Si está en uso, 409 <c>catalog.option.in_use</c> sin efecto: la salida es desactivarla
/// con el PUT. No se desactiva sola, porque un DELETE que responde error y cambia algo es peor que
/// cualquiera de las dos cosas por separado.
/// </summary>
public sealed class DeleteOptionHandler(
    IOptionDefinitionRepository repository,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeleteOptionCommand, OptionDeletedResult>
{
    public async Task<OptionDeletedResult> HandleAsync(
        DeleteOptionCommand command,
        CancellationToken cancellationToken)
    {
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);

        var option = await repository.FindAsync(
            command.TenantId, new OptionDefinitionId(command.OptionId), cancellationToken)
            ?? throw OptionNotFound.For(command.OptionId);

        var usage = await repository.GetUsageAsync(command.TenantId, cancellationToken);
        if (usage.IsInUse(option))
        {
            throw OptionInUse.Create();
        }

        var now = clock.UtcNow;
        repository.Remove(option);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.option.deleted",
            option.Id.ToString(),
            "success",
            now);

        // Si entre la consulta y el commit una variante empezó a usarla, las FK RESTRICT de
        // product_options y product_variant_option_values lo frenan y CatalogUnitOfWork lo
        // traduce al mismo 409.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new OptionDeletedResult(true);
    }
}
