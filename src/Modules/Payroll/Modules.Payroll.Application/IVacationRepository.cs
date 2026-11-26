using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>Las vacaciones disfrutadas. Todo método lleva el tenant: una lectura nunca cruza de tenant.</summary>
public interface IVacationRepository
{
    /// <summary>Rastreado. Nulo si no existe en el tenant.</summary>
    Task<Vacation?> FindAsync(Guid tenantId, VacationId id, CancellationToken cancellationToken);

    /// <summary>Las del empleado en esa empresa, sin seguimiento: del inicio más reciente al más viejo, con desempate por id.</summary>
    Task<IReadOnlyList<Vacation>> ListByEmployeeAsync(
        Guid tenantId, Guid companyId, Guid employeeId, CancellationToken cancellationToken);

    /// <summary>Company-wide, no tracking: start ≤ <paramref name="lastDay"/> and end ≥ <paramref name="firstDay"/>.</summary>
    Task<IReadOnlyList<Vacation>> ListOverlappingAsync(
        Guid tenantId, Guid companyId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);

    void Add(Vacation vacation);

    void Remove(Vacation vacation);
}
