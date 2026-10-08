using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <param name="From">Fecha local del tenant; se corta con StartOfDayUtc.</param>
/// <param name="To">Fecha local del tenant, incluida; se corta con EndOfDayExclusiveUtc.</param>
public sealed record ListPosSalesQuery(
    Guid TenantId, Guid? SessionId, DateOnly? From, DateOnly? To, string? Status, string? Number, int Page, int PageSize)
    : IQuery<PosPage<PosSaleListItemResponse>>;

public sealed class ListPosSalesValidator : AbstractValidator<ListPosSalesQuery>
{
    public ListPosSalesValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 100);
        RuleFor(query => query.Status)
            .Must(status => status is null || Enum.GetNames<PosSaleStatus>().Contains(status, StringComparer.Ordinal))
            .WithMessage("The status must be Completed or Voided.");
        RuleFor(query => query.Number).MaximumLength(20);
        RuleFor(query => query)
            .Must(query => query.From is null || query.To is null || query.From <= query.To)
            .WithName("from").WithMessage("from must not be after to.");
    }
}

public sealed class ListPosSalesHandler(
    IPosSaleRepository sales,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    IValidator<ListPosSalesQuery> validator)
    : IQueryHandler<ListPosSalesQuery, PosPage<PosSaleListItemResponse>>
{
    public async Task<PosPage<PosSaleListItemResponse>> HandleAsync(ListPosSalesQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.SaleRead);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        // Sin pos.register.read filtra al cajero llamador aunque mande el sessionId de otro.
        var cashier = await PosScope.CashierFilterAsync(membershipDirectory, executionContext, query.TenantId, cancellationToken);
        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);

        var (rows, total) = await sales.ListAsync(
            new PosSaleFilter(
                query.TenantId,
                cashier,
                query.SessionId is { } sessionId ? new CashSessionId(sessionId) : null,
                query.From is { } from ? calendar.StartOfDayUtc(from) : null,
                query.To is { } to ? calendar.EndOfDayExclusiveUtc(to) : null,
                query.Status is { } status ? Enum.Parse<PosSaleStatus>(status) : null,
                string.IsNullOrWhiteSpace(query.Number) ? null : query.Number.Trim(),
                query.Page,
                query.PageSize),
            cancellationToken);

        var items = rows.Select(row =>
        {
            var (voidable, reason) = PosVoidability.For(row.Sale.Status, row.SessionStatus);
            return new PosSaleListItemResponse(
                row.Sale.Id.Value,
                row.Sale.SaleNumber,
                calendar.ToLocal(row.Sale.CreatedAt),
                row.CashierName,
                row.Sale.CustomerName,
                row.Sale.Total,
                row.Sale.Currency,
                row.Sale.Status.ToString(),
                row.Sale.Payments.OrderBy(payment => payment.Position).Select(payment => payment.Method.ToString()).ToArray(),
                voidable,
                reason);
        }).ToArray();

        return new PosPage<PosSaleListItemResponse>(items, query.Page, query.PageSize, total);
    }
}
