using BuildingBlocks.Application;
using Modules.Support.Domain;

namespace Modules.Support.Application;

public sealed record GetPublicConsultQuery(string? TenantSlug) : IQuery<SupportPublicConsultDto>;

/// <summary>
/// Lo que la página de consulta necesita para dibujarse (owner, 2026-11-15, E1): el nombre del tenant, su logo, el color de la versión
/// vigente del formulario, publicada o no, y la empresa que responde de esa misma versión (spec 2026-11-15 §9.3); sin ninguna versión,
/// null en los dos y la página usa su color por defecto. Resuelve con <see cref="SupportPublicResolver.ResolveForConsultAsync"/>: no
/// exige formulario publicado.
/// </summary>
public sealed class GetPublicConsultHandler(
    SupportPublicResolver resolver,
    ISupportFormRepository forms,
    ISupportTenantBranding branding,
    ISupportOrganizationDirectory organizations)
    : IQueryHandler<GetPublicConsultQuery, SupportPublicConsultDto>
{
    public async Task<SupportPublicConsultDto> HandleAsync(GetPublicConsultQuery query, CancellationToken cancellationToken)
    {
        var tenant = await resolver.ResolveForConsultAsync(query.TenantSlug, cancellationToken);
        var form = await forms.CurrentAsync(tenant.TenantId, cancellationToken);
        return new SupportPublicConsultDto(
            tenant.Name,
            await branding.LogoUrlAsync(tenant.TenantId, cancellationToken),
            form?.AccentColor,
            form is null ? null : await SupportPublicOrganization.OfAsync(organizations, form, cancellationToken));
    }
}

/// <summary>
/// The company that answers, as the portal shows it (spec 2026-11-15 §9.3, plan L32): the one the given form version chose, read by
/// <see cref="ISupportOrganizationDirectory"/> filtered by the version's tenant. A deactivated company is still shown; none chosen, or
/// one that does not resolve for the tenant, is null and the portal does not paint the line.
/// </summary>
internal static class SupportPublicOrganization
{
    public static async Task<SupportPublicOrganizationDto?> OfAsync(
        ISupportOrganizationDirectory organizations, SupportFormVersion form, CancellationToken cancellationToken) =>
        form.OrganizationCompanyId is { } companyId
            && await organizations.FindAsync(form.TenantId, companyId, cancellationToken) is { } company
            ? new SupportPublicOrganizationDto(company.Name, company.DocumentTypeCode, company.DocumentNumber)
            : null;
}
