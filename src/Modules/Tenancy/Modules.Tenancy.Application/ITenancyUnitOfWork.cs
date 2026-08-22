namespace Modules.Tenancy.Application;

public interface ITenancyUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
