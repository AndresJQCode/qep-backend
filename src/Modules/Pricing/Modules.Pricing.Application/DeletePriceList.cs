using BuildingBlocks.Application;
using Modules.Pricing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pricing.Application;

public sealed record DeletePriceListCommand(Guid TenantId, Guid PriceListId)
    : ICommand<PriceListDeletedResult>;

// BuildingBlocks no tiene un ICommand sin resultado. Mismo patron que
// ClientClassificationDeletedResult en Customers: el endpoint responde 204 y no lo mira.
public sealed record PriceListDeletedResult(bool Deleted);

/// <summary>
/// Borra una lista de precios **si nadie la usa**: ni una escala de producto de Catalog, ni una
/// asignación de cliente de Customers. A diferencia de <c>DeleteClientClassificationHandler</c>
/// —que pregunta a su propio módulo, porque Customer y ClientClassification viven juntos—, acá
/// las dos referencias cruzan de módulo y ninguna tiene FK real que Postgres pueda imponer, así
/// que <see cref="IPriceListUsageLookup"/> es la única red: sin ella, borrar una lista en uso
/// dejaría escalas de producto y asignaciones de cliente apuntando a un id que ya no existe.
/// </summary>
public sealed class DeletePriceListHandler(
    IPriceListRepository repository,
    IPriceListUsageLookup usageLookup,
    IPricingUnitOfWork unitOfWork,
    IPricingAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeletePriceListCommand, PriceListDeletedResult>
{
    public async Task<PriceListDeletedResult> HandleAsync(
        DeletePriceListCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes que nada: quien tiene el permiso para otro tenant tiene que llevarse un
        // 403 antes de averiguar si el id existe aca.
        PricingAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, PricingPermissions.PriceListManage);

        var priceListId = new PriceListId(command.PriceListId);

        // FindAsync filtra por tenant, asi que una lista ajena sale por aca como 404 y nunca
        // llega al borrado.
        var priceList = await repository.FindAsync(command.TenantId, priceListId, cancellationToken)
            ?? throw PriceListNotFound.For(command.PriceListId);

        if (await usageLookup.IsUsedByAnyProductAsync(
                command.TenantId, command.PriceListId, cancellationToken))
        {
            throw new PricingDomainException(
                "pricing.price_list.in_use",
                "The price list cannot be deleted because at least one product has a price " +
                "scale for it.");
        }

        if (await usageLookup.IsAssignedToAnyCustomerAsync(
                command.TenantId, command.PriceListId, cancellationToken))
        {
            throw new PricingDomainException(
                "pricing.price_list.in_use",
                "The price list cannot be deleted because at least one customer has it assigned.");
        }

        var now = clock.UtcNow;
        repository.Remove(priceList);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "pricing.price_list.deleted",
            priceList.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new PriceListDeletedResult(true);
    }
}
