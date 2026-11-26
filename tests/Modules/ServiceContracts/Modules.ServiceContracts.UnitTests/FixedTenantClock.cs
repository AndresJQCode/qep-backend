using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.UnitTests;

/// <summary>La «hoy» de un tenant fija, para los validadores que acotan fechas contra ella.</summary>
internal sealed class FixedTenantClock(DateTimeOffset utcNow, string timeZone = "America/Bogota") : ITenantClock
{
    public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(new TenantCalendar(utcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZone)));
}
