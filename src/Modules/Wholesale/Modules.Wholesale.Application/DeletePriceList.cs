using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

public sealed record DeletePriceListCommand(Guid TenantId, Guid PriceListId, long ExpectedVersion) : ICommand<bool>;

/// <summary>
/// Borra una lista que ningún distribuidor usa, en ningún estado (409 <c>in_use</c>). La FK <c>ON DELETE RESTRICT</c> de
/// <c>distributors.price_list_id</c> cubre la carrera contra una invitación simultánea (la unidad de trabajo la traduce al
/// mismo 409).
/// </summary>
public sealed class DeletePriceListHandler(
    IExecutionContext executionContext,
    IClock clock,
    IPriceListRepository priceLists,
    IDistributorUsage usage,
    IWholesaleUnitOfWork unitOfWork,
    IWholesaleAuditPublisher auditPublisher)
    : ICommandHandler<DeletePriceListCommand, bool>
{
    public async Task<bool> HandleAsync(DeletePriceListCommand command, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, command.TenantId, WholesalePermissions.PriceListManage);
        var list = await priceLists.GetAsync(command.TenantId, new PriceListId(command.PriceListId), cancellationToken)
            ?? throw WholesaleConflicts.PriceListNotFound(command.PriceListId);
        WholesaleConflicts.EnsureVersion(list.Version, command.ExpectedVersion);

        var counts = await usage.CountByPriceListAsync(command.TenantId, cancellationToken);
        if (counts.GetValueOrDefault(list.Id) > 0)
        {
            throw WholesaleConflicts.PriceListInUse();
        }

        priceLists.Remove(list);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            WholesaleAuditActions.PriceListDeleted,
            WholesaleAuditActions.Resources.PriceList,
            list.Id.ToString(),
            "success",
            [],
            clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }
}
