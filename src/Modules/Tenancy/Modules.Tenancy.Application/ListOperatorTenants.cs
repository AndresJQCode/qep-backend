using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record ListOperatorTenantsQuery(TenantId TenantId, string? Search, int Page, int PageSize)
    : IQuery<OperatorTenantPageDto>;

/// <summary>Spec 2026-10-08 §5: un valor fuera de rango es <c>422 validation.failed</c>, nunca 500.</summary>
public sealed class ListOperatorTenantsValidator : AbstractValidator<ListOperatorTenantsQuery>
{
    public ListOperatorTenantsValidator()
    {
        RuleFor(query => query.Search).MaximumLength(OperatorInput.MaxSearchLength);
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, OperatorInput.MaxPageSize);
    }
}

public sealed class ListOperatorTenantsHandler(
    IOperatorTenantReader reader,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IValidator<ListOperatorTenantsQuery> validator)
    : IQueryHandler<ListOperatorTenantsQuery, OperatorTenantPageDto>
{
    public async Task<OperatorTenantPageDto> HandleAsync(ListOperatorTenantsQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, OperatorPermissions.TenantsRead, operatorTenant);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var listing = await reader.ListAsync(query.Search, query.Page, query.PageSize, cancellationToken);
        var items = listing.Rows
            .Select(row => new OperatorTenantListItemDto(
                row.TenantId, row.Slug, row.DisplayName, row.Status.ToString(), row.CreatedAt,
                row.ActiveModules, TenantModuleKeys.All.Count, operatorTenant.IsOperator(row.TenantId)))
            .ToArray();
        return new OperatorTenantPageDto(
            items, listing.Total, query.Page, query.PageSize,
            new OperatorTenantSummaryDto(listing.AllTenants, listing.WithoutModules, listing.Inactive));
    }
}
