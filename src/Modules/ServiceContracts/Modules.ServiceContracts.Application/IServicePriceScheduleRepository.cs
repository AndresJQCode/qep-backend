using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Application;

/// <summary>Los cambios de precio programados (spec reajuste §5): a lo sumo uno pendiente por contrato.</summary>
public interface IServicePriceScheduleRepository
{
    void Add(ServicePriceSchedule schedule);

    /// <summary>Con tracking: quien lo lee lo aplica o lo cancela.</summary>
    Task<ServicePriceSchedule?> FindPendingAsync(Guid tenantId, ServiceContractId serviceId, CancellationToken cancellationToken);

    /// <summary>Sin tracking: para pintar.</summary>
    Task<IReadOnlyList<ServicePriceSchedule>> ListPendingByServicesAsync(
        Guid tenantId, IReadOnlyCollection<ServiceContractId> serviceIds, CancellationToken cancellationToken);

    /// <summary>Sin tracking.</summary>
    Task<IReadOnlyList<ServicePriceSchedule>> ListPendingByTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Los años de IPC que usa algún pendiente: una tasa en uso no se edita.</summary>
    Task<IReadOnlyList<int>> ListPendingIpcYearsAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Todos los del contrato, en cualquier estado: borrar el contrato se los lleva.</summary>
    Task RemoveByServiceAsync(Guid tenantId, ServiceContractId serviceId, CancellationToken cancellationToken);
}
