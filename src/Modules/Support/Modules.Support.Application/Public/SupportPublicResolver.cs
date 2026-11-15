using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <summary>El tenant que atiende el formulario público, con la versión vigente (publicada) del formulario.</summary>
public sealed record SupportPublicTenant(Guid TenantId, string Slug, string Name, string Country, SupportFormVersion Form);

/// <summary>El tenant que atiende la página de consulta: no exige formulario publicado (owner, 2026-11-15, E1).</summary>
public sealed record SupportPublicConsultTenant(Guid TenantId, string Slug, string Name);

/// <summary>
/// Del slug de la ruta al tenant que atiende el grupo público (plan F3, precedente <c>StorefrontResolver</c>), con dos entradas:
/// <list type="bullet">
/// <item><see cref="ResolveAsync"/>, la del formulario: slug normalizado → tenant <c>Active</c> → módulo <c>support</c> prendido →
/// formulario publicado. La usan <c>GET /form</c>, la geografía, radicar y los adjuntos.</item>
/// <item><see cref="ResolveForConsultAsync"/>, la de la consulta: lo mismo sin exigir formulario publicado (owner, 2026-11-15, E1:
/// todo solicitante tiene un enlace de consulta que funciona desde el primer día). La usan <c>GET /consult</c>, consultar un ticket y
/// «Reenviar PIN».</item>
/// </list>
/// En las dos, un slug que no es de nadie, un tenant inactivo, el módulo apagado y (en la del formulario) un formulario sin publicar dan
/// el mismo 404 <c>support.public.not_found</c>, con el mismo cuerpo. En la entrada del formulario, quien sondea slugs no distingue un
/// tenant en preparación de uno que no existe. La de la consulta sí lo revela, por decisión del owner (E1): <c>GET /consult</c> responde
/// 200 con el nombre a todo tenant activo con Soporte prendido, publique o no su formulario, porque la página de consulta tiene que
/// funcionar desde el primer día. Falla CERRADO: un tenant sin fila de módulos tampoco se sirve. No lee el contexto de ejecución ni la
/// sesión: el grupo es anónimo.
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

    /// <summary>La entrada del formulario: exige la versión vigente publicada.</summary>
    public async Task<SupportPublicTenant> ResolveAsync(string? slug, CancellationToken cancellationToken)
    {
        var (tenantId, normalized) = await ServedTenantAsync(slug, cancellationToken);
        var form = await forms.CurrentAsync(tenantId.Value, cancellationToken);
        if (form is not { IsPublished: true })
        {
            throw NotFound();
        }

        var name = await tenants.GetDisplayNameAsync(tenantId, cancellationToken) ?? throw NotFound();
        var regional = await tenants.GetRegionalSettingsAsync(tenantId, cancellationToken) ?? throw NotFound();
        return new SupportPublicTenant(tenantId.Value, normalized, name, regional.Country, form);
    }

    /// <summary>La entrada de la consulta: no mira el formulario.</summary>
    public async Task<SupportPublicConsultTenant> ResolveForConsultAsync(string? slug, CancellationToken cancellationToken)
    {
        var (tenantId, normalized) = await ServedTenantAsync(slug, cancellationToken);
        var name = await tenants.GetDisplayNameAsync(tenantId, cancellationToken) ?? throw NotFound();
        return new SupportPublicConsultTenant(tenantId.Value, normalized, name);
    }

    // Lo común a las dos entradas: slug válido, tenant activo y módulo prendido.
    private async Task<(TenantId TenantId, string Slug)> ServedTenantAsync(string? slug, CancellationToken cancellationToken)
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

        return (tenantId, normalized);
    }

    // Un solo mensaje para todas las causas: el cuerpo es idéntico salvo el traceId.
    internal static ResourceNotFoundException NotFound() =>
        new(SupportErrorCodes.PublicNotFound, "The support form was not found.");
}
