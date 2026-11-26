using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>Los períodos de nómina. Todo método lleva el tenant: una lectura nunca cruza de tenant.</summary>
public interface IPayrollPeriodRepository
{
    /// <summary>Con capturas y eventos, rastreado: lo que se le haga se guarda con el unit of work. Nulo si no existe en el tenant.</summary>
    Task<PayrollPeriod?> FindAsync(Guid tenantId, PayrollPeriodId id, CancellationToken cancellationToken);

    /// <summary>Los de la empresa sin capturas ni eventos, sin seguimiento, por <c>start_date</c> descendente.</summary>
    Task<IReadOnlyList<PayrollPeriod>> ListAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);

    /// <summary>Sin capturas ni eventos, sin seguimiento: sólo para mirar su estado (la primera quincena de una segunda).</summary>
    Task<PayrollPeriod?> FindByStartAsync(Guid tenantId, Guid companyId, DateOnly start, CancellationToken cancellationToken);

    /// <summary>Rastreado y con sus eventos (sin capturas): para marcar desactualizada la segunda quincena desde la primera. Nulo si no existe.</summary>
    Task<PayrollPeriod?> FindTrackedByStartAsync(Guid tenantId, Guid companyId, DateOnly start, CancellationToken cancellationToken);

    /// <summary>Las capturas del período en la base, sin seguimiento: la lectura que no depende de si el período se cargó con ellas.</summary>
    Task<IReadOnlyList<PeriodInput>> ListInputsAsync(Guid tenantId, PayrollPeriodId periodId, CancellationToken cancellationToken);

    void Add(PayrollPeriod period);

    /// <summary>Las empresas con algún período: la lista de empresas incluye las inactivas que lo tienen (M-1).</summary>
    Task<IReadOnlyList<Guid>> ListCompanyIdsAsync(Guid tenantId, CancellationToken cancellationToken);
}
