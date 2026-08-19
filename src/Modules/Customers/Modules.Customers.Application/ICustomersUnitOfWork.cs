namespace Modules.Customers.Application;

public interface ICustomersUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
