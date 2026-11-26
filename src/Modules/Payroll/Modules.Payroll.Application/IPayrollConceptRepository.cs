using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>Los conceptos propios de una empresa (los del sistema no son filas). Todo método lleva el tenant y la empresa.</summary>
public interface IPayrollConceptRepository
{
    /// <summary>Rastreado. Nulo si no existe en esa empresa de ese tenant.</summary>
    Task<PayrollConcept?> FindAsync(Guid tenantId, Guid companyId, PayrollConceptId id, CancellationToken cancellationToken);

    /// <summary>Todos los de la empresa, archivados incluidos, sin seguimiento.</summary>
    Task<IReadOnlyList<PayrollConcept>> ListAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);

    void Add(PayrollConcept concept);
}
