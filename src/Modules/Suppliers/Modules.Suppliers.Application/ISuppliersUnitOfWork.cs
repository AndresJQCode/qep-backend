namespace Modules.Suppliers.Application;

public interface ISuppliersUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
