namespace Modules.Pricing.Application;

public interface IPricingUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
