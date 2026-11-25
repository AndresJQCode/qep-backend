using Modules.Siigo.Domain;

namespace Modules.Siigo.Application.Products;

/// <summary>El producto de Siigo al que está vinculada una variante: la foto que se guardó al vincular (id, código y nombre), no una
/// lectura de Siigo.</summary>
public sealed record SiigoProductRefDto(string Id, string Code, string Name);

/// <summary>Una fila de la tabla de vínculos (spec 2026-11-18 §5.3, paso 6): la variante de QEP y, si la tiene, su producto de Siigo con
/// el origen del vínculo (<c>AutoCode</c> o <c>Manual</c>, por nombre). Sin vínculo, <c>SiigoProduct</c> y <c>Origin</c> son nulos.</summary>
public sealed record SiigoVariantLinkDto(
    Guid VariantId,
    string DisplayName,
    string Code,
    bool IsActive,
    SiigoProductRefDto? SiigoProduct,
    string? Origin);

/// <summary>La cobertura se informa y no bloquea: variantes activas vinculadas sobre variantes activas.</summary>
public sealed record SiigoCoverageDto(int Linked, int TotalActive);

/// <summary>Una página de la tabla con la cobertura de toda la conexión (no de la página): la pantalla la pinta arriba de la tabla.</summary>
public sealed record SiigoVariantLinksPageDto(
    IReadOnlyList<SiigoVariantLinkDto> Items, int Page, int PageSize, int Total, SiigoCoverageDto Coverage);

/// <summary>El resultado de «Vincular por código»: cuántas variantes quedaron vinculadas y cuántas se saltaron porque su código casa con
/// más de un producto activo de Siigo (no se sabe cuál es).</summary>
public sealed record SiigoAutoLinkResultDto(int Linked, int SkippedAmbiguous);

/// <summary>Lo que comparten los casos de uso de vínculos de productos.</summary>
internal static class SiigoProductLinksSupport
{
    /// <summary>De a cuántas filas pagina la tabla. Fijo: la pantalla no lo elige.</summary>
    public const int PageSize = 50;

    /// <summary>La última página cuyo desplazamiento (<c>(page - 1) * PageSize</c>) cabe en un <c>int</c>. Una mayor se recorta a ésta y
    /// la respuesta devuelve la que se usó: sin el tope el producto se desborda y el catálogo recibe un desplazamiento negativo
    /// (500).</summary>
    public const int MaxPage = int.MaxValue / PageSize;

    public const string StepName = "products";

    public const string FilterAll = "all";
    public const string FilterUnlinked = "unlinked";
    public const string FilterLinked = "linked";

    public static SiigoVariantLinkDto ToRow(SiigoVariantRef variant, SiigoVariantLink? link) =>
        new(
            variant.VariantId,
            variant.DisplayName,
            variant.Code,
            variant.IsActive,
            link is null ? null : new SiigoProductRefDto(link.SiigoProductId, link.SiigoProductCode, link.SiigoProductName),
            link?.Origin.ToString());

    public static async Task<SiigoCoverageDto> CoverageAsync(
        ISiigoVariantCatalog catalog, SiigoSettings settings, CancellationToken cancellationToken)
    {
        var linkedIds = settings.VariantLinks.Select(link => link.VariantId).ToArray();
        return new SiigoCoverageDto(
            linkedIds.Length == 0 ? 0 : await catalog.CountActiveAsync(settings.TenantId, linkedIds, cancellationToken),
            await catalog.CountActiveAsync(settings.TenantId, null, cancellationToken));
    }
}
