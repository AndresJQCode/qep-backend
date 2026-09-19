using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

internal static class TenantMappings
{
    public static TenantSettingsDto ToSettingsDto(this Tenant tenant, ITenantLogoStorage logoStorage) =>
        new(
            tenant.Id,
            tenant.DisplayName,
            tenant.DefaultCulture,
            tenant.TimeZone,
            tenant.DateFormat,
            tenant.Version,
            LogoOf(tenant, logoStorage));

    private static TenantLogoDto? LogoOf(Tenant tenant, ITenantLogoStorage logoStorage) =>
        tenant.LogoFileId is { } fileId
            ? new TenantLogoDto(fileId, tenant.LogoPublicKey is { } key ? logoStorage.GetUrl(key) : null)
            : null;
}
