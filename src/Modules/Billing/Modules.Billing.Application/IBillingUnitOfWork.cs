namespace Modules.Billing.Application;

public interface IBillingUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
