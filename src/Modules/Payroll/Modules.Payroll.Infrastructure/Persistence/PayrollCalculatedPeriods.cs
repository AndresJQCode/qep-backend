using Microsoft.EntityFrameworkCore;
using Modules.Payroll.Application;

namespace Modules.Payroll.Infrastructure.Persistence;

/// <summary>Una sola consulta sobre <c>payroll.periods</c>, sin tenant: sólo las dos columnas que necesita la consola de valores legales.</summary>
internal sealed class PayrollCalculatedPeriods(PayrollDbContext dbContext) : IPayrollCalculatedPeriods
{
    public async Task<IReadOnlyList<PayrollCalculatedPeriod>> ListOfAllTenantsAsync(CancellationToken cancellationToken) =>
        await dbContext.Periods
            .AsNoTracking()
            .Where(period => period.LegalValuesDate != null)
            .Select(period => new PayrollCalculatedPeriod(period.LegalValuesDate!.Value, period.CompanyId))
            .ToListAsync(cancellationToken);
}
