using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Application;

public interface IChargeRepository
{
    void AddRange(IEnumerable<Charge> charges);

    Task<Charge?> FindAsync(Guid tenantId, Guid chargeId, CancellationToken cancellationToken);

    /// <summary>Los <c>Pending</c> de un servicio; los usa cancelar (todos pasan a <c>Cancelled</c>, §3.2).</summary>
    Task<IReadOnlyList<Charge>> ListPendingByServiceAsync(ServiceContractId serviceId, CancellationToken cancellationToken);

    /// <summary>Los <c>Pending</c> de varios servicios de una vez: arma <c>nextCharge</c>, <c>openCount</c> y los totales de una página de servicios sin un viaje por fila.</summary>
    Task<IReadOnlyList<Charge>> ListPendingByServicesAsync(
        IReadOnlyCollection<ServiceContractId> serviceIds, CancellationToken cancellationToken);

    /// <summary>Todos los <c>Pending</c> del tenant (servicios <c>Active</c> o <c>Paused</c>): los candidatos del mensaje del día (§3.3, paso 4).</summary>
    Task<IReadOnlyList<Charge>> ListPendingByTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<(IReadOnlyList<Charge> Items, int Total)> ListAsync(ChargeQuery query, CancellationToken cancellationToken);

    /// <summary>Todos los cobros de un servicio, por <c>dueDate</c> desc, para el detalle.</summary>
    Task<IReadOnlyList<Charge>> ListByServiceAsync(ServiceContractId serviceId, CancellationToken cancellationToken);

    /// <summary>Las cifras de <c>summary</c> del tenant entero, sin los filtros de la lista; <paramref name="today"/> decide qué está vencido.</summary>
    Task<ChargeSummaryDto> SummaryAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken);

    /// <summary>
    /// El último mensaje de cada cobro, a partir de su último <c>reminder_step</c> (§2). Un cobro sin avisos no aparece en el
    /// diccionario. La clave es el id del cobro.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ReminderDto>> ListLastRemindersAsync(
        IReadOnlyCollection<Guid> chargeIds, CancellationToken cancellationToken);
}
