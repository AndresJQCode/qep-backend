using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

public sealed record DeleteDistributorCommand(Guid TenantId, Guid DistributorId, long ExpectedVersion) : ICommand<bool>;

/// <summary>Borra una relación todavía invitada (D-14), con su invitación: el link pasa a 404. Fuera de eso, 409.</summary>
public sealed class DeleteDistributorHandler(
    IExecutionContext executionContext,
    IClock clock,
    IDistributorRepository distributors,
    IWholesaleUnitOfWork unitOfWork,
    IWholesaleAuditPublisher auditPublisher)
    : ICommandHandler<DeleteDistributorCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteDistributorCommand command, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, command.TenantId, WholesalePermissions.DistributorManage);
        var distributor = await distributors.GetAsync(command.TenantId, new DistributorId(command.DistributorId), cancellationToken)
            ?? throw WholesaleConflicts.DistributorNotFound(command.DistributorId);
        WholesaleConflicts.EnsureVersion(distributor.Version, command.ExpectedVersion);
        WholesaleConflicts.EnsureAllowed(distributor, DistributorAction.Delete);
        distributor.EnsureDeletable();

        distributors.Remove(distributor);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            WholesaleAuditActions.DistributorDeleted,
            WholesaleAuditActions.Resources.Distributor,
            distributor.Id.ToString(),
            "success",
            [],
            clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }
}
