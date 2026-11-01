namespace Modules.Loans.Application;

public interface ILoansUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
