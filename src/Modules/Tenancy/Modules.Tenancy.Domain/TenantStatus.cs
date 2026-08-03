namespace Modules.Tenancy.Domain;

public enum TenantStatus
{
    Provisioning = 1,
    Active = 2,
    Suspended = 3,
    Failed = 4,
    Decommissioning = 5,
    Decommissioned = 6
}
