using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

public sealed record ListCashSessionsQuery(
    Guid TenantId, DateOnly? From, DateOnly? To, string? Status, int Page, int PageSize)
    : IQuery<PosPage<PosSessionSummaryResponse>>;

public sealed class ListCashSessionsValidator : AbstractValidator<ListCashSessionsQuery>
{
    public ListCashSessionsValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
        RuleFor(query => query.Status)
            .Must(status => status is null || Enum.GetNames<CashSessionStatus>().Contains(status, StringComparer.Ordinal))
            .WithMessage("The status must be Open or Closed.");
        RuleFor(query => query)
            .Must(query => query.From is null || query.To is null || query.From <= query.To)
            .WithName("from").WithMessage("from must not be after to.");
    }
}

public sealed class ListCashSessionsHandler(
    ICashSessionRepository sessions,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    IValidator<ListCashSessionsQuery> validator)
    : IQueryHandler<ListCashSessionsQuery, PosPage<PosSessionSummaryResponse>>
{
    public async Task<PosPage<PosSessionSummaryResponse>> HandleAsync(ListCashSessionsQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.SaleRead);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        var cashier = await PosScope.CashierFilterAsync(membershipDirectory, executionContext, query.TenantId, cancellationToken);
        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);

        var (items, total) = await sessions.ListAsync(
            new CashSessionFilter(
                query.TenantId,
                cashier,
                query.From is { } from ? calendar.StartOfDayUtc(from) : null,
                query.To is { } to ? calendar.EndOfDayExclusiveUtc(to) : null,
                query.Status is { } status ? Enum.Parse<CashSessionStatus>(status) : null,
                query.Page,
                query.PageSize),
            cancellationToken);

        return new PosPage<PosSessionSummaryResponse>(
            items.Select(session => PosSessionMapping.ToSummary(session, calendar)).ToArray(),
            query.Page, query.PageSize, total);
    }
}
