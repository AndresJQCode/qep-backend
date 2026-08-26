namespace Modules.Quotations.Application;

public interface IQuotationsUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
