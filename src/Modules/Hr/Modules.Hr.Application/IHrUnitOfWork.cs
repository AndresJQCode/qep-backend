namespace Modules.Hr.Application;

public interface IHrUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
