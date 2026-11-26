namespace Modules.Payroll.Application;

public interface IPayrollUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
