using BuildingBlocks.Application;
using Modules.Pricing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pricing.Application;

public sealed record ActivatePriceListCommand(Guid TenantId, Guid PriceListId)
    : ICommand<PriceListDto>;

// Sin validador, por la misma razon que DeactivatePriceList: el comando no lleva texto libre.
// Activar algo ya activo lo rechaza el agregado, que es donde vive esa regla.
public sealed class ActivatePriceListHandler(
    IPriceListRepository repository,
    IPricingUnitOfWork unitOfWork,
    IPricingAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ActivatePriceListCommand, PriceListDto>
{
    public async Task<PriceListDto> HandleAsync(
        ActivatePriceListCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de leer el repositorio, no despues: consultar primero le confirma al
        // llamador que el id existe.
        PricingAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, PricingPermissions.PriceListManage);

        var priceList = await repository.FindAsync(
            command.TenantId, new PriceListId(command.PriceListId), cancellationToken)
            ?? throw PriceListNotFound.For(command.PriceListId);

        var now = clock.UtcNow;
        priceList.Activate(now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "pricing.price_list.activated",
            priceList.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return priceList.ToDto();
    }
}
