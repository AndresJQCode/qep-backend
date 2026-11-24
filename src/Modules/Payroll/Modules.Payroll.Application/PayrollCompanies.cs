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

    /// <summary>
    /// Una empresa inactiva se lee pero no se cambia: no abre quincenas ni guarda configuración (decisión del owner 10, 2026-11-22).
    /// </summary>
    public static void EnsureActive(PayrollCompanyRef company, string message)
    {
        ArgumentNullException.ThrowIfNull(company);
        if (!company.IsActive)
        {
            throw new PayrollDomainException(PayrollErrorCodes.CompanyInactive, message);
        }
    }

    public static PayrollCompanyDto ToDto(PayrollCompanyRef company) =>
        new(company.Id, company.Name, company.TaxId, company.IsActive);
}
