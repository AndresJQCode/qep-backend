using BuildingBlocks.Application;
using Modules.Pricing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pricing.Application;

public sealed record DeactivatePriceListCommand(Guid TenantId, Guid PriceListId)
    : ICommand<PriceListDto>;

// Sin validador: el comando no lleva texto libre. Desactivar dos veces lo rechaza el agregado,
// que es donde va esa regla.
public sealed class DeactivatePriceListHandler(
    IPriceListRepository repository,
    IPricingUnitOfWork unitOfWork,
    IPricingAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<DeactivatePriceListCommand, PriceListDto>
{
    public async Task<PriceListDto> HandleAsync(
        DeactivatePriceListCommand command,
        CancellationToken cancellationToken)
    {
        PricingAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, PricingPermissions.PriceListManage);

        var priceList = await repository.FindAsync(
            command.TenantId, new PriceListId(command.PriceListId), cancellationToken)
            ?? throw PriceListNotFound.For(command.PriceListId);

        var now = clock.UtcNow;
        priceList.Deactivate(now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "pricing.price_list.deactivated",
            priceList.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return priceList.ToDto();
    }
}
