namespace Modules.Companies.Application;

public interface ICompaniesUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
