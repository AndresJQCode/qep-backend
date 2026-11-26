using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>La configuración de nómina por empresa. Todo método lleva el tenant: una lectura nunca cruza de tenant.</summary>
public interface ICompanyPayrollSettingsRepository
{
    /// <summary>Rastreada: lo que se le haga se guarda con el unit of work. Nula si la empresa nunca la guardó.</summary>
    Task<CompanyPayrollSettings?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);

    void Add(CompanyPayrollSettings settings);

    /// <summary>Las empresas con configuración guardada: la lista de empresas incluye las inactivas que la tienen (M-1).</summary>
    Task<IReadOnlyList<Guid>> ListCompanyIdsAsync(Guid tenantId, CancellationToken cancellationToken);
}
