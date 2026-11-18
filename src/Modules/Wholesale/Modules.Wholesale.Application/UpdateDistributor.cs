using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

public sealed record UpdateDistributorCommand(
    Guid TenantId,
    Guid DistributorId,
    long ExpectedVersion,
    Guid PriceListId,
    Guid AdvisorId,
    Guid BillingCompanyId)
    : ICommand<DistributorDetailDto>;

/// <summary>Lista, asesor y empresa facturadora, en cualquier estado. Orden: 403 → 422 → 404 → 412 → las tres verificaciones.</summary>
public sealed class UpdateDistributorHandler(
    IExecutionContext executionContext,
    IClock clock,
    IPriceListRepository priceLists,
    IDistributorRepository distributors,
    IWholesaleMemberDirectory members,
    IWholesaleCompanyLookup companies,
    IWholesaleUnitOfWork unitOfWork,
    IWholesaleAuditPublisher auditPublisher,
    DistributorComposer composer,
    IValidator<UpdateDistributorCommand> validator)
    : ICommandHandler<UpdateDistributorCommand, DistributorDetailDto>
{
    private static readonly string[] WrittenFields = ["priceListId", "advisorId", "billingCompanyId"];

    public async Task<DistributorDetailDto> HandleAsync(UpdateDistributorCommand command, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, command.TenantId, WholesalePermissions.DistributorManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var distributor = await distributors.GetAsync(command.TenantId, new DistributorId(command.DistributorId), cancellationToken)
            ?? throw WholesaleConflicts.DistributorNotFound(command.DistributorId);
        WholesaleConflicts.EnsureVersion(distributor.Version, command.ExpectedVersion);
        WholesaleConflicts.EnsureAllowed(distributor, DistributorAction.Edit);
        await DistributorTerms.EnsureValidAsync(
            priceLists, members, companies, command.TenantId, command.PriceListId, command.AdvisorId, command.BillingCompanyId,
            cancellationToken);

        var now = clock.UtcNow;
        distributor.ChangeTerms(new PriceListId(command.PriceListId), command.AdvisorId, command.BillingCompanyId, now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            WholesaleAuditActions.DistributorUpdated,
            WholesaleAuditActions.Resources.Distributor,
            distributor.Id.ToString(),
            "success",
            WrittenFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.DetailAsync(distributor, cancellationToken);
    }
}
