using BuildingBlocks.Application;
using FluentValidation;
using Modules.Siigo.Application.Settings;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Products;

/// <summary><c>GET …/products?search=&amp;filter=all|unlinked|linked&amp;page=</c> (<c>siigo.settings.manage</c>).</summary>
public sealed record ListSiigoVariantLinksQuery(Guid TenantId, Guid ConnectionId, string? Search, string? Filter, int? Page)
    : IQuery<SiigoVariantLinksPageDto>;

/// <summary>La forma de la consulta: <c>filter</c> es uno de los tres nombres exactos y <c>page</c>, de 1 en adelante (422
/// <c>validation.failed</c> con el campo).</summary>
public sealed class ListSiigoVariantLinksValidator : AbstractValidator<ListSiigoVariantLinksQuery>
{
    public ListSiigoVariantLinksValidator()
    {
        RuleFor(query => query.Filter)
            .Must(filter => filter is SiigoProductLinksSupport.FilterAll or SiigoProductLinksSupport.FilterUnlinked or SiigoProductLinksSupport.FilterLinked)
            .When(query => query.Filter is not null)
            .WithMessage("The filter must be all, unlinked or linked.")
            .OverridePropertyName("filter");
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1).When(query => query.Page is not null).OverridePropertyName("page");
        RuleFor(query => query.Search).MaximumLength(200).OverridePropertyName("search");
    }
}

/// <summary>
/// Spec 2026-11-18 §5.3, paso 6. En orden: tenant, permiso y módulo (403); la forma (422); la conexión del tenant (403). Las variantes
/// salen del catálogo (<see cref="ISiigoVariantCatalog"/>) y el vínculo de la configuración: «vinculadas» y «sin vincular» se resuelven
/// pasándole al catálogo los ids vinculados, sin cruzar los dos esquemas. La cobertura es la de toda la conexión.
/// </summary>
public sealed class ListSiigoVariantLinksHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoConnectionDirectory connections,
    ISiigoSettingsRepository settingsRepository,
    ISiigoVariantCatalog catalog,
    IValidator<ListSiigoVariantLinksQuery> validator) : IQueryHandler<ListSiigoVariantLinksQuery, SiigoVariantLinksPageDto>
{
    public async Task<SiigoVariantLinksPageDto> HandleAsync(ListSiigoVariantLinksQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SiigoSettingsSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        await SiigoSettingsSupport.ConnectionAsync(connections, query.TenantId, query.ConnectionId, cancellationToken);
        var settings = await settingsRepository.FindOrCreateAsync(query.TenantId, query.ConnectionId, withVariantLinks: true, cancellationToken)
            ?? throw SiigoAuthorization.Denied();

        var page = Math.Min(query.Page ?? 1, SiigoProductLinksSupport.MaxPage);
        var linkedIds = settings.VariantLinks.Select(link => link.VariantId).ToArray();
        var found = await catalog.SearchAsync(
            query.TenantId,
            query.Search,
            query.Filter == SiigoProductLinksSupport.FilterLinked ? linkedIds : null,
            query.Filter == SiigoProductLinksSupport.FilterUnlinked ? linkedIds : null,
            (page - 1) * SiigoProductLinksSupport.PageSize,
            SiigoProductLinksSupport.PageSize,
            cancellationToken);

        var links = settings.VariantLinks.ToDictionary(link => link.VariantId);
        return new SiigoVariantLinksPageDto(
            [.. found.Items.Select(variant => SiigoProductLinksSupport.ToRow(variant, links.GetValueOrDefault(variant.VariantId)))],
            page,
            SiigoProductLinksSupport.PageSize,
            found.TotalResults,
            await SiigoProductLinksSupport.CoverageAsync(catalog, settings, cancellationToken));
    }
}
