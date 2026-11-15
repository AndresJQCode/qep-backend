using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <summary>El tenant que atiende el formulario público, con la versión vigente (publicada) del formulario.</summary>
public sealed record SupportPublicTenant(Guid TenantId, string Slug, string Name, string Country, SupportFormVersion Form);

/// <summary>
/// Del slug de la ruta al tenant que atiende el formulario público (plan F3, precedente <c>StorefrontResolver</c>): slug normalizado →
/// tenant <c>Active</c> → módulo <c>support</c> prendido → formulario publicado. Un slug que no es de nadie, un tenant inactivo, el
/// módulo apagado y un formulario sin publicar dan el mismo 404 <c>support.public.not_found</c>, con el mismo cuerpo: quien sondea
/// slugs no distingue un tenant en preparación de uno que no existe. Falla CERRADO: un tenant sin fila de módulos tampoco se sirve. No
/// lee el contexto de ejecución ni la sesión: el grupo es anónimo.
/// </summary>
public sealed class SupportPublicResolver(
    ITenantSlugDirectory slugs,
    ITenantDirectory tenants,
    ITenantModules tenantModules,
    ISupportFormRepository forms)
{
    // Los límites de Tenant.ValidateSlug: un slug fuera de ellos no puede existir y no vale una consulta.
    private const int SlugMinLength = 3;

    private const int SlugMaxLength = 63;

    public async Task<SupportPublicTenant> ResolveAsync(string? slug, CancellationToken cancellationToken)
    {
        var normalized = slug?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized.Length is < SlugMinLength or > SlugMaxLength)
        {
            throw NotFound();
        }

        var tenantId = await slugs.FindIdBySlugAsync(normalized, cancellationToken) ?? throw NotFound();
        if (await tenants.GetStatusAsync(tenantId, cancellationToken) != TenantStatus.Active)
        {
            throw NotFound();
        }

        var modules = await tenantModules.FindAsync(tenantId.Value, cancellationToken);
        if (modules is null || !modules.IsEnabled(TenantModuleKeys.Support))
        {
            throw NotFound();
        }

        var form = await forms.CurrentAsync(tenantId.Value, cancellationToken);
        if (form is not { IsPublished: true })
        {
            throw NotFound();
        }

        var name = await tenants.GetDisplayNameAsync(tenantId, cancellationToken) ?? throw NotFound();
        var regional = await tenants.GetRegionalSettingsAsync(tenantId, cancellationToken) ?? throw NotFound();
        return new SupportPublicTenant(tenantId.Value, normalized, name, regional.Country, form);
    }

    // Un solo mensaje para todas las causas: el cuerpo es idéntico salvo el traceId.
    internal static ResourceNotFoundException NotFound() =>
        new(SupportErrorCodes.PublicNotFound, "The support form was not found.");
}
