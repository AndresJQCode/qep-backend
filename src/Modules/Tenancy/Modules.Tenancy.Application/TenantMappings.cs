using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

internal static class TenantMappings
{
    public static TenantSettingsDto ToSettingsDto(this Tenant tenant) =>
        new(
            tenant.Id,
            tenant.DisplayName,
            tenant.DefaultCulture,
            tenant.TimeZone,
            tenant.DateFormat,
            tenant.Version);
}
