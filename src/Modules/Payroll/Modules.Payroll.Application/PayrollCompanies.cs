using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>
/// Lo que Payroll hace con las empresas de Companies, sobre el puerto <see cref="IPayrollHrDirectory"/>. Una empresa que no existe
/// en el tenant de quien llama es 404, igual que un id inventado.
/// </summary>
public sealed class PayrollCompanies(IPayrollHrDirectory directory)
{
    /// <summary>La empresa, activa o no: el handler decide si una inactiva puede abrir períodos.</summary>
    public async Task<PayrollCompanyRef> RequireAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        await directory.FindCompanyAsync(tenantId, companyId, cancellationToken)
            ?? throw PayrollNotFound.Company(companyId);

    public static PayrollCompanyDto ToDto(PayrollCompanyRef company) =>
        new(company.Id, company.Name, company.TaxId, company.IsActive);
}
