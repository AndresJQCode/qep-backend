using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure;

/// <summary>
/// Lee la moneda guardada del tenant a través de <see cref="ITenantDirectory"/>. Pública sólo para
/// probarla sin <c>InternalsVisibleTo</c>, igual que <see cref="TenantModuleDefaults"/>.
/// </summary>
public sealed class TenantDefaultCurrency(ITenantDirectory directory) : ITenantDefaultCurrency
{
    public async Task<string> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await directory.GetDefaultCurrencyAsync(new TenantId(tenantId), cancellationToken)
        ?? throw new ResourceNotFoundException(
            "tenancy.tenant.not_found",
            "Tenant was not found.");
}
