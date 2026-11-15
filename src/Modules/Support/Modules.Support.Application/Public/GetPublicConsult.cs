using BuildingBlocks.Application;

namespace Modules.Support.Application;

public sealed record GetPublicConsultQuery(string? TenantSlug) : IQuery<SupportPublicConsultDto>;

/// <summary>
/// Lo que la página de consulta necesita para dibujarse (owner, 2026-11-15, E1): el nombre del tenant, su logo y el color de la versión
/// vigente del formulario, publicada o no; sin ninguna versión, null y la página usa su color por defecto. Resuelve con
/// <see cref="SupportPublicResolver.ResolveForConsultAsync"/>: no exige formulario publicado.
/// </summary>
public sealed class GetPublicConsultHandler(
    SupportPublicResolver resolver,
    ISupportFormRepository forms,
    ISupportTenantBranding branding)
    : IQueryHandler<GetPublicConsultQuery, SupportPublicConsultDto>
{
    public async Task<SupportPublicConsultDto> HandleAsync(GetPublicConsultQuery query, CancellationToken cancellationToken)
    {
        var tenant = await resolver.ResolveForConsultAsync(query.TenantSlug, cancellationToken);
        var form = await forms.CurrentAsync(tenant.TenantId, cancellationToken);
        return new SupportPublicConsultDto(
            tenant.Name, await branding.LogoUrlAsync(tenant.TenantId, cancellationToken), form?.AccentColor);
    }
}
