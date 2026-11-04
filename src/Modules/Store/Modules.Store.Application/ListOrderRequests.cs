using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Store.Application;

/// <param name="View"><c>Untaken</c> (por defecto), <c>TakenByMe</c> o <c>All</c>.</param>
public sealed record ListOrderRequestsQuery(Guid TenantId, string? View, int? Page, int? PageSize)
    : IQuery<StoreOrderRequestPageDto>;

public sealed class ListOrderRequestsValidator : AbstractValidator<ListOrderRequestsQuery>
{
    public const int MaxPageSize = 100;

    public const int DefaultPageSize = 25;

    public ListOrderRequestsValidator()
    {
        RuleFor(query => query.View)
            .Must(view => view is null || Enum.TryParse<StoreInboxView>(view, ignoreCase: true, out _))
            .WithMessage("La vista debe ser Untaken, TakenByMe o All.");
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1).When(query => query.Page is not null);
        RuleFor(query => query.PageSize).InclusiveBetween(1, MaxPageSize).When(query => query.PageSize is not null);
    }
}

public sealed class ListOrderRequestsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IStoreMemberDirectory members,
    IStoreOrderRequestRepository requests,
    ITenantClock tenantClock,
    OrderRequestRowComposer composer,
    IValidator<ListOrderRequestsQuery> validator)
    : IQueryHandler<ListOrderRequestsQuery, StoreOrderRequestPageDto>
{
    public async Task<StoreOrderRequestPageDto> HandleAsync(ListOrderRequestsQuery query, CancellationToken cancellationToken)
    {
        await StoreAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, StorePermissions.OrderManage, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var view = query.View is null ? StoreInboxView.Untaken : Enum.Parse<StoreInboxView>(query.View, ignoreCase: true);
        // Una página fuera de rango se corrige y no se rechaza: la bandeja relee y sigue, sin un 422 que arreglar.
        var page = StoreInboxFilter.ClampPage(query.Page ?? 1);
        var pageSize = query.PageSize ?? ListOrderRequestsValidator.DefaultPageSize;
        var caller = await members.ResolveCallerAsync(query.TenantId, cancellationToken);

        var (items, total) = await requests.ListAsync(
            new StoreInboxFilter(query.TenantId, view, caller.MembershipId, page, pageSize), cancellationToken);
        var now = (await tenantClock.GetAsync(query.TenantId, cancellationToken)).UtcNow;
        var rows = await composer.ComposeAsync(query.TenantId, items, now, cancellationToken);
        return new StoreOrderRequestPageDto(rows, total, page, pageSize);
    }
}
