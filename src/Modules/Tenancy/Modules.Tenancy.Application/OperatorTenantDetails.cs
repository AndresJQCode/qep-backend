using BuildingBlocks.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>El detalle de §5. Lo usan el GET y, con el estado ya commiteado, los dos POST.</summary>
internal static class OperatorTenantDetails
{
    public static async Task<OperatorTenantDetailDto> LoadAsync(
        IOperatorTenantReader reader, IOperatorTenant operatorTenant, TenantId tenantId, CancellationToken cancellationToken)
    {
        var snapshot = await reader.FindAsync(tenantId, cancellationToken)
            ?? throw new ResourceNotFoundException("tenancy.tenant.not_found", "The tenant was not found.");
        return From(snapshot, operatorTenant.IsOperator(snapshot.TenantId));
    }

    private static OperatorTenantDetailDto From(OperatorTenantSnapshot snapshot, bool isOperator)
    {
        var rows = snapshot.Modules.ToDictionary(module => module.Key);
        var effective = TenantModuleSet.FromStored(
            snapshot.Modules.Where(module => module.Status == TenantModuleStatus.Active).Select(module => module.Key));
        var modules = TenantModuleKeys.All
            .Select(key =>
            {
                var row = rows.GetValueOrDefault(key);
                return new OperatorTenantModuleDto(
                    key.Value,
                    row is null ? OperatorTenantModuleDto.NoRowStatus : TenantChangeVocabulary.ToText(row.Status),
                    effective.IsEnabled(key),
                    TenantModuleKeys.DependenciesOf(key).Select(dependency => dependency.Value).ToArray(),
                    row?.StatusChangedAt,
                    row?.Source,
                    snapshot.LastModuleReasons.TryGetValue(key, out var reason) ? TenantChangeVocabulary.ToText(reason) : null);
            })
            .ToArray();

        return new OperatorTenantDetailDto(
            snapshot.TenantId, snapshot.Slug, snapshot.DisplayName, snapshot.CreatedAt, snapshot.Status.ToString(),
            snapshot.LastStatusChange?.OccurredAt,
            snapshot.LastStatusChange is { } change ? TenantChangeVocabulary.ToText(change.Reason) : null,
            snapshot.Version, isOperator, modules);
    }
}
