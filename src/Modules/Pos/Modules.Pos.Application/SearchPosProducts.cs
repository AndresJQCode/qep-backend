using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>
/// La cuadrícula del POS (spec 2026-10-20 §5): productos activos que coinciden por nombre, por
/// <c>Code</c> de alguna variante o por código de barras, cada uno con sus variantes vendibles.
/// <c>Total</c> cuenta productos. <c>CategoryId</c> filtra por la categoría y sus descendientes
/// (spec 2026-10-26 §3.4); sin regla de validación: un id que no es del tenant da página vacía.
/// </summary>
public sealed record SearchPosProductsQuery(Guid TenantId, string? Search, int Page, int PageSize, Guid? CategoryId = null)
    : IQuery<PosPage<PosCatalogProductResponse>>;

public sealed class SearchPosProductsValidator : AbstractValidator<SearchPosProductsQuery>
{
    public SearchPosProductsValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 60);
        RuleFor(query => query.Search).MaximumLength(200);
    }
}

public sealed class SearchPosProductsHandler(
    IPosProductLookup products,
    ICashSessionRepository sessions,
    IMembershipDirectory membershipDirectory,
    ITenantDefaultCurrency tenantDefaultCurrency,
    IExecutionContext executionContext,
    IValidator<SearchPosProductsQuery> validator)
    : IQueryHandler<SearchPosProductsQuery, PosPage<PosCatalogProductResponse>>
{
    public async Task<PosPage<PosCatalogProductResponse>> HandleAsync(
        SearchPosProductsQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.SaleCreate);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var currency = await PosSellingCurrency.ResolveAsync(
            sessions, membershipDirectory, tenantDefaultCurrency, executionContext, query.TenantId, cancellationToken);
        var (items, total) = await products.SearchAsync(
            query.TenantId, query.Search?.Trim(), query.CategoryId, query.Page, query.PageSize, cancellationToken);
        return new PosPage<PosCatalogProductResponse>(
            items.Select(item => PosProductMapping.ToResponse(item, currency)).ToArray(), query.Page, query.PageSize, total);
    }
}
