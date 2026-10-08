using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

public sealed record SearchPosProductsQuery(Guid TenantId, string? Search, int Page, int PageSize)
    : IQuery<PosPage<PosProductResponse>>;

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
    : IQueryHandler<SearchPosProductsQuery, PosPage<PosProductResponse>>
{
    public async Task<PosPage<PosProductResponse>> HandleAsync(
        SearchPosProductsQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.SaleCreate);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var currency = await PosSellingCurrency.ResolveAsync(
            sessions, membershipDirectory, tenantDefaultCurrency, executionContext, query.TenantId, cancellationToken);
        var (items, total) = await products.SearchAsync(
            query.TenantId, query.Search?.Trim(), query.Page, query.PageSize, cancellationToken);
        return new PosPage<PosProductResponse>(
            items.Select(item => PosProductMapping.ToResponse(item, currency)).ToArray(), query.Page, query.PageSize, total);
    }
}
