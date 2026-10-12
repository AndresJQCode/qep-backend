using BuildingBlocks.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Application;

namespace Modules.Customers.Application;

public sealed record SetMarketingConsentCommand(Guid TenantId, Guid CustomerId, bool OptedOut) : ICommand<MarketingConsentDto>;

/// <summary>
/// Spec 2026-10-11 decisión 10: una persona prende o apaga «No quiere recibir promociones» en la ficha. Repetir el
/// valor actual no cambia nada ni deja rastro de auditoría: no es una mutación. Sin validador: el comando no lleva
/// texto libre.
/// </summary>
public sealed class SetMarketingConsentHandler(
    ICustomerRepository customers,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<SetMarketingConsentCommand, MarketingConsentDto>
{
    public async Task<MarketingConsentDto> HandleAsync(SetMarketingConsentCommand command, CancellationToken cancellationToken)
    {
        CustomersAuthorization.EnsureAuthorized(executionContext, command.TenantId, CustomersPermissions.CustomerManage);
        var customer = await customers.FindAsync(command.TenantId, new CustomerId(command.CustomerId), cancellationToken)
            ?? throw CustomerNotFound.For(command.CustomerId);

        var now = clock.UtcNow;
        var changed = command.OptedOut
            ? customer.OptOutOfMarketing(MarketingConsentSource.Manual, now)
            : customer.OptInToMarketing();
        if (changed)
        {
            auditPublisher.Publish(
                command.TenantId, executionContext.SubjectId, CustomerAuditActions.MarketingConsentChanged, customer.Id.ToString(), "success", now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return customer.ToConsentDto();
    }
}
