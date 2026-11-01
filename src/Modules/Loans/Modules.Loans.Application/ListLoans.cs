using BuildingBlocks.Application;
using FluentValidation;
using Modules.Loans.Domain;
using Modules.Tenancy.Application;

namespace Modules.Loans.Application;

/// <summary>
/// <c>Status</c> es <c>active | settled</c>, sin distinguir mayúsculas; el endpoint manda <c>active</c> si el cliente no lo pidió.
/// La lista va por <c>disbursedOn</c> descendente.
/// </summary>
public sealed record ListLoansQuery(Guid TenantId, string Status, string? Search, int Page, int PageSize) : IQuery<LoanPageDto>
{
    public const string DefaultStatus = "active";

    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;
}

public sealed class ListLoansValidator : AbstractValidator<ListLoansQuery>
{
    public ListLoansValidator()
    {
        RuleFor(query => query.Status)
            .Must(status => ListLoansHandler.TryParseStatus(status, out _))
            .WithMessage("Status must be active or settled.");
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, ListLoansQuery.MaxPageSize);
    }
}

/// <summary>
/// El resumen es del tenant entero y no cambia con <c>status</c>, <c>search</c> ni la página. El interés causado se suma en
/// memoria sobre los préstamos con capital: depende de los abonos de cada uno y no se puede sumar en SQL, y un tenant tiene pocos
/// préstamos (precedente <c>ListServices</c>).
/// </summary>
public sealed class ListLoansHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ILoansCustomerLookup customers,
    ILoanRepository loans,
    ILoanCapitalPaymentRepository capitalPayments,
    LoanDtoComposer composer,
    IValidator<ListLoansQuery> validator)
    : IQueryHandler<ListLoansQuery, LoanPageDto>
{
    internal static bool TryParseStatus(string? text, out LoanStatus status)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "active":
                status = LoanStatus.Active;
                return true;
            case "settled":
                status = LoanStatus.Settled;
                return true;
            default:
                status = default;
                return false;
        }
    }

    public async Task<LoanPageDto> HandleAsync(ListLoansQuery query, CancellationToken cancellationToken)
    {
        await LoansAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, LoansPermissions.Read, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        if (!TryParseStatus(query.Status, out var status))
        {
            throw new InvalidOperationException($"Status '{query.Status}' passed validation but is not a loan status.");
        }

        var today = (await tenantClock.GetAsync(query.TenantId, cancellationToken)).Today;
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        // El nombre del cliente vive en el esquema de Customers: se resuelve acá y el repositorio filtra por esos ids o por el
        // nombre del préstamo.
        var customerIdsBySearch = search is null
            ? null
            : await customers.SearchIdsAsync(query.TenantId, search, cancellationToken);
        var (page, total) = await loans.ListAsync(
            new LoanListFilter(query.TenantId, status, search, customerIdsBySearch, query.Page, query.PageSize), cancellationToken);

        var items = await composer.ComposeManyAsync(query.TenantId, page, today, cancellationToken);
        var summary = await SummarizeAsync(query.TenantId, today, cancellationToken);

        return new LoanPageDto(items, total, query.Page, query.PageSize, summary);
    }

    private async Task<LoanSummaryDto> SummarizeAsync(Guid tenantId, DateOnly today, CancellationToken cancellationToken)
    {
        var totals = await loans.TotalsAsync(tenantId, cancellationToken);

        var accruing = await loans.ListAccruingAsync(tenantId, cancellationToken);
        var paymentsByLoan = (await capitalPayments.ListByLoansAsync(tenantId, accruing.Select(loan => loan.Id).ToArray(), cancellationToken))
            .ToLookup(payment => payment.LoanId);
        var accrued = accruing
            .Select(loan => (loan.Currency, Amount: LoanAccrual.ToDate(loan, paymentsByLoan[loan.Id], today)?.Amount ?? 0m));

        return new LoanSummaryDto(
            totals.ActiveCount,
            totals.SettledCount,
            totals.PendingInstallmentCount,
            LoanCurrencyAmounts.Of(totals.OutstandingByCurrency.Select(total => (total.Currency, total.Amount))),
            LoanCurrencyAmounts.Of(totals.PendingByCurrency.Select(total => (total.Currency, total.Amount))),
            LoanCurrencyAmounts.Of(accrued));
    }
}
