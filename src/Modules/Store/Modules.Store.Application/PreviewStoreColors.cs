using BuildingBlocks.Application;
using Modules.Store.Domain;
using Modules.Tenancy.Application;

namespace Modules.Store.Application;

/// <summary>
/// Previsualiza los colores sin escribir nada y sin <c>If-Match</c> (como el <c>POST …/preview</c> de Loans). Usa el mismo
/// <see cref="StoreColorScheme.Resolve"/> que la tienda guardada y la pública, así que lo que el backoffice muestra es exactamente lo
/// que la tienda pinta. Un contraste bajo se marca y nunca se rechaza.
/// </summary>
public sealed record PreviewStoreColorsQuery(Guid TenantId, string? PrimaryColor, string? OnPrimaryColor, string? AccentTextColor)
    : IQuery<StoreColorPreviewDto>;

public sealed class PreviewStoreColorsHandler(IExecutionContext executionContext, ITenantModules tenantModules)
    : IQueryHandler<PreviewStoreColorsQuery, StoreColorPreviewDto>
{
    public async Task<StoreColorPreviewDto> HandleAsync(PreviewStoreColorsQuery query, CancellationToken cancellationToken)
    {
        await StoreAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, StorePermissions.SettingsManage, cancellationToken);

        var resolved = StoreColorScheme.Resolve(
            StoreColorScheme.ParseRequired(query.PrimaryColor),
            StoreColorScheme.ParseOptional(query.OnPrimaryColor),
            StoreColorScheme.ParseOptional(query.AccentTextColor));
        return StoreColorMapping.ToPreview(resolved);
    }
}
