using BuildingBlocks.Application;
using FluentValidation;
using Modules.Loans.Domain;
using Modules.Tenancy.Application;

namespace Modules.Loans.Application;

/// <summary>«Por cobrar»: las cuotas pendientes del tenant, por <c>periodEnd</c> ascendente.</summary>
public sealed record ListLoanInstallmentsQuery(Guid TenantId, string? Search, int Page, int PageSize) : IQuery<LoanInstallmentPageDto>
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;
}

public sealed class ListLoanInstallmentsValidator : AbstractValidator<ListLoanInstallmentsQuery>
{
    public ListLoanInstallmentsValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, ListLoanInstallmentsQuery.MaxPageSize);
    }
}

/// <summary>
/// El resumen es del tenant entero y no cambia con <c>search</c> ni con la página: <c>oldest</c> es la cuota más antigua aunque la
/// búsqueda la deje fuera de <c>items</c>. Préstamos, clientes y encargados se resuelven por lote, nunca uno por fila.
/// </summary>
public sealed class ListLoanInstallmentsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ILoansCustomerLookup customers,
    ILoansMemberDirectory members,
    ILoanRepository loans,
    ILoanInstallmentRepository installments,
    IValidator<ListLoanInstallmentsQuery> validator)
    : IQueryHandler<ListLoanInstallmentsQuery, LoanInstallmentPageDto>
{
    public async Task<LoanInstallmentPageDto> HandleAsync(ListLoanInstallmentsQuery query, CancellationToken cancellationToken)
    {
        await LoansAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, LoansPermissions.Read, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        // El nombre del cliente vive en el esquema de Customers: se resuelve acá y el repositorio filtra por esos ids o por el
        // nombre del préstamo.
        var customerIdsBySearch = search is null
            ? null
            : await customers.SearchIdsAsync(query.TenantId, search, cancellationToken);
        var (page, total) = await installments.ListPendingAsync(
            new PendingInstallmentFilter(query.TenantId, search, customerIdsBySearch, query.Page, query.PageSize), cancellationToken);
        var totals = await installments.PendingSummaryAsync(query.TenantId, cancellationToken);

        // Las cuotas de la página y la más antigua del tenant salen de los mismos viajes.
        var loanIds = page.Select(row => row.LoanId).ToList();
        if (totals.Oldest is { } oldestRow)
        {
            loanIds.Add(oldestRow.LoanId);
        }

        var loansById = (await loans.FindManyAsync(query.TenantId, loanIds.Distinct().ToArray(), cancellationToken))
            .ToDictionary(loan => loan.Id);
        var customerRefs = (await customers.FindManyAsync(
                query.TenantId, loansById.Values.Select(loan => loan.CustomerId).Distinct().ToArray(), cancellationToken))
            .ToDictionary(customer => customer.Id);
        var assignees = (await members.FindAsync(
                query.TenantId,
                loansById.Values.Where(loan => loan.AssigneeMemberId.HasValue).Select(loan => loan.AssigneeMemberId!.Value).Distinct().ToArray(),
                cancellationToken))
            .ToDictionary(member => member.MemberId);

        var items = page
            .Select(row => ToRow(row, loansById[row.LoanId], customerRefs, assignees))
            .ToList();

        LoanOldestInstallmentDto? oldest = null;
        if (totals.Oldest is { } found)
        {
            var customerId = loansById[found.LoanId].CustomerId;
            oldest = new LoanOldestInstallmentDto(
                found.PeriodEnd, found.Amount, found.Currency, customerRefs.GetValueOrDefault(customerId)?.Name ?? LoanDtoComposer.UnknownName);
        }

        var summary = new LoanInstallmentSummaryDto(
            totals.PendingCount,
            LoanCurrencyAmounts.Of(totals.PendingByCurrency.Select(total => (total.Currency, total.Amount))),
            oldest);
        return new LoanInstallmentPageDto(items, total, query.Page, query.PageSize, summary);
    }

    private static LoanInstallmentRowDto ToRow(
        LoanInstallment installment,
        Loan loan,
        IReadOnlyDictionary<Guid, LoansCustomerRef> customerRefs,
        IReadOnlyDictionary<Guid, LoansMemberRef> assignees)
    {
        var customer = customerRefs.GetValueOrDefault(loan.CustomerId);
        return new LoanInstallmentRowDto(
            installment.Id,
            loan.Id.Value,
            loan.Name,
            customer is null
                ? new LoanCustomerDto(loan.CustomerId, LoanDtoComposer.UnknownName, null, null)
                : new LoanCustomerDto(customer.Id, customer.Name, customer.PhoneE164, customer.Email),
            loan.AssigneeMemberId is { } assigneeId ? LoanDtoComposer.ToMemberDto(assigneeId, assignees.GetValueOrDefault(assigneeId)) : null,
            installment.PeriodStart,
            installment.PeriodEnd,
            installment.Amount,
            installment.Currency,
            installment.IsImported);
    }
}
