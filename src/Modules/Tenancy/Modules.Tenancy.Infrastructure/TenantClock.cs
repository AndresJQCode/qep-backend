using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure;

/// <summary>
/// Combina <see cref="IClock"/>, el huso guardado del tenant y <see cref="TimeZoneInfo"/>. Scoped:
/// el huso se lee una vez por tenant por scope, así un request que corta varias fechas —o un proceso
/// que recorre tenants— no repite la consulta. El instante se lee de <see cref="IClock"/> en cada
/// llamada. <c>Tenant.TimeZone</c> ya viene validado como ID IANA (<c>Tenant.ValidateTimeZone</c>).
/// </summary>
internal sealed class TenantClock(IClock clock, ITenantDirectory directory) : ITenantClock
{
    private readonly Dictionary<Guid, TimeZoneInfo> _timeZones = [];

    public async Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!_timeZones.TryGetValue(tenantId, out var timeZone))
        {
            var timeZoneId = await directory.GetTimeZoneAsync(new TenantId(tenantId), cancellationToken)
                ?? throw new ResourceNotFoundException(
                    "tenancy.tenant.not_found",
                    "Tenant was not found.");
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            _timeZones[tenantId] = timeZone;
        }

        return new TenantCalendar(clock.UtcNow, timeZone);
    }
}
