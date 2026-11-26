using Microsoft.EntityFrameworkCore;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Infrastructure.Persistence;

internal sealed class ServicePriceScheduleRepository(ServiceContractsDbContext dbContext) : IServicePriceScheduleRepository
{
    public void Add(ServicePriceSchedule schedule) => dbContext.ServicePriceSchedules.Add(schedule);

    public Task<ServicePriceSchedule?> FindPendingAsync(
        Guid tenantId, ServiceContractId serviceId, CancellationToken cancellationToken) =>
        dbContext.ServicePriceSchedules.FirstOrDefaultAsync(
            schedule => schedule.TenantId == tenantId
                && schedule.ServiceId == serviceId
                && schedule.Status == PriceScheduleStatus.Pending,
            cancellationToken);

    public async Task<IReadOnlyList<ServicePriceSchedule>> ListPendingByServicesAsync(
        Guid tenantId, IReadOnlyCollection<ServiceContractId> serviceIds, CancellationToken cancellationToken)
    {
        if (serviceIds.Count == 0)
        {
            return [];
        }

        var ids = serviceIds.ToArray();
        return await dbContext.ServicePriceSchedules
            .AsNoTracking()
            .Where(schedule => schedule.TenantId == tenantId
                && ids.Contains(schedule.ServiceId)
                && schedule.Status == PriceScheduleStatus.Pending)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ServicePriceSchedule>> ListPendingByTenantAsync(
        Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.ServicePriceSchedules
            .AsNoTracking()
            .Where(schedule => schedule.TenantId == tenantId && schedule.Status == PriceScheduleStatus.Pending)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<int>> ListPendingIpcYearsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.ServicePriceSchedules
            .AsNoTracking()
            .Where(schedule => schedule.TenantId == tenantId
                && schedule.Source == PriceScheduleSource.Ipc
                && schedule.Status == PriceScheduleStatus.Pending
                && schedule.IpcYear != null)
            .Select(schedule => schedule.IpcYear!.Value)
            .Distinct()
            .OrderBy(year => year)
            .ToListAsync(cancellationToken);

    public async Task RemoveByServiceAsync(Guid tenantId, ServiceContractId serviceId, CancellationToken cancellationToken) =>
        dbContext.ServicePriceSchedules.RemoveRange(
            await dbContext.ServicePriceSchedules
                .Where(schedule => schedule.TenantId == tenantId && schedule.ServiceId == serviceId)
                .ToListAsync(cancellationToken));
}
