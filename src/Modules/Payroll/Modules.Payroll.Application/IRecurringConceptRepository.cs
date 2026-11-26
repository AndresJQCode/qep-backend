using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>Los conceptos recurrentes de la ficha de cada empleado. Todo método lleva el tenant: una lectura nunca cruza de tenant.</summary>
public interface IRecurringConceptRepository
{
    /// <summary>Rastreado. Nulo si no existe en el tenant.</summary>
    Task<RecurringConcept?> FindAsync(Guid tenantId, RecurringConceptId id, CancellationToken cancellationToken);

    /// <summary>Los del empleado en esa empresa, sin seguimiento.</summary>
    Task<IReadOnlyList<RecurringConcept>> ListByEmployeeAsync(
        Guid tenantId, Guid companyId, Guid employeeId, CancellationToken cancellationToken);

    /// <summary>Lectura masiva para el planificador de la liquidación: todos los de la empresa, sin seguimiento.</summary>
    Task<IReadOnlyList<RecurringConcept>> ListByCompanyAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);

    void Add(RecurringConcept recurring);
}
