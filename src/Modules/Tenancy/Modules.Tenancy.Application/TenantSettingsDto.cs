using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record TenantSettingsDto(
    TenantId TenantId,
    string DisplayName,
    string DefaultCulture,
    string TimeZone,
    string DateFormat,
    long Version);
