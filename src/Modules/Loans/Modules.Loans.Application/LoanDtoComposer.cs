using Modules.Loans.Domain;
using Modules.Tenancy.Application;

namespace Modules.Loans.Application;

/// <summary>Un total por moneda, nunca convertido, en orden de catálogo y sin monedas en cero (contrato de <see cref="LoanCurrencyAmountDto"/>).</summary>
internal static class LoanCurrencyAmounts
{
    public static IReadOnlyList<LoanCurrencyAmountDto> Of(IEnumerable<(string Currency, decimal Amount)> amounts) => amounts
        .GroupBy(item => item.Currency, StringComparer.Ordinal)
        .Select(group => new LoanCurrencyAmountDto(group.Key, group.Sum(item => item.Amount)))
        .Where(total => total.Amount != 0m)
        .OrderBy(total => Currencies.OrderOf(total.Currency))
        .ThenBy(total => total.Currency, StringComparer.Ordinal)
        .ToList();
}

/// <summary>
/// Arma las respuestas de préstamo del contrato: el <see cref="LoanDetailDto"/> de leer, crear, editar y operar, y los
/// <see cref="LoanDto"/> de las listas. Lo reutilizan todos los mutadores. Lo que la pantalla necesita saber (si se puede abonar,
/// qué abono se anula, si se pueden editar las condiciones) se deriva acá y nunca se escribe: Backend for Frontend.
/// </summary>
public sealed class LoanDtoComposer(
    ILoansCustomerLookup customers,
    ILoansMemberDirectory members,
    ILoanInstallmentRepository installments,
    ILoanCapitalPaymentRepository capitalPayments)
{
    /// <summary>Lo que el contrato muestra de un cliente que ya no se resuelve.</summary>
    internal const string UnknownName = "Sin nombre";

    /// <summary>
    /// El detalle de un préstamo ya cargado (y acotado a su tenant) por quien llama. Se llama después de guardar: lee cuotas y
    /// abonos de la base, no del contexto.
    /// </summary>
    public async Task<LoanDetailDto> ComposeDetailAsync(Loan loan, DateOnly today, CancellationToken cancellationToken)
    {
        var tenantId = loan.TenantId;
        var installmentRows = await installments.ListByLoanAsync(tenantId, loan.Id, cancellationToken);
        var payments = await capitalPayments.ListByLoansAsync(tenantId, [loan.Id], cancellationToken);

        var customer = await customers.FindAsync(tenantId, loan.CustomerId, cancellationToken);
        // El encargado y quienes marcaron cada cuota como pagada salen en una sola consulta al directorio.
        var memberIds = installmentRows
            .Select(row => row.PaidByMemberId)
            .Append(loan.AssigneeMemberId)
            .OfType<Guid>();
        var membersById = await FindMembersAsync(tenantId, memberIds, cancellationToken);

        // Las cifras de «por cobrar» salen de las mismas cuotas que se listan: el detalle suma lo que muestra.
        var pending = installmentRows.Where(row => row.Status == LoanInstallmentStatus.Pending).ToList();
        var accrual = LoanAccrual.ToDate(loan, payments, today);
        var loanDto = ToLoanDto(loan, customer, membersById, new PendingTotal(pending.Count, pending.Sum(row => row.Amount)), accrual, today);

        var block = CapitalPaymentBlockOf(loan, pending.Count, today);
        var hasMovement = LoanRules.HasMovement(installmentRows, payments);

        return new LoanDetailDto(
            loanDto,
            installmentRows.Select(row => ToInstallmentDto(row, membersById)).ToList(),
            // Del más reciente al más viejo, anulados incluidos.
            payments
                .OrderByDescending(payment => payment.PaidOn)
                .ThenByDescending(payment => payment.CreatedAt)
                .ThenByDescending(payment => payment.Id)
                .Select(ToCapitalPaymentDto)
                .ToList(),
            accrual is null
                ? null
                : new LoanCurrentPeriodDto(
                    accrual.Period.Start, accrual.Period.End, accrual.Period.Days, accrual.Amount, accrual.Segments.Select(ToSegmentDto).ToList()),
            block,
            block is null ? new LoanPaymentWindowDto(LoanCapitalOperations.OpenFrom(loan), today) : null,
            // POST void rechaza con installment_generated mientras haya un período cerrado sin generar: no se ofrece.
            HasUngeneratedClosedPeriod(loan, today) ? null : LoanCapitalOperations.Voidable(loan, payments)?.Id,
            !hasMovement,
            LoanRules.CanDelete(installmentRows, payments));
    }

    /// <summary>
    /// Los <see cref="LoanDto"/> de una página de préstamos, en el mismo orden: un viaje por lote (clientes, encargados, cuotas
    /// pendientes y abonos), nunca uno por fila.
    /// </summary>
    public async Task<IReadOnlyList<LoanDto>> ComposeManyAsync(
        Guid tenantId, IReadOnlyList<Loan> loans, DateOnly today, CancellationToken cancellationToken)
    {
        if (loans.Count == 0)
        {
            return [];
        }

        var ids = loans.Select(loan => loan.Id).ToArray();
        var customerRefs = (await customers.FindManyAsync(
                tenantId, loans.Select(loan => loan.CustomerId).Distinct().ToArray(), cancellationToken))
            .ToDictionary(customer => customer.Id);
        var assignees = await FindMembersAsync(
            tenantId, loans.Where(loan => loan.AssigneeMemberId.HasValue).Select(loan => loan.AssigneeMemberId!.Value), cancellationToken);
        var pendingByLoan = await installments.PendingTotalsAsync(tenantId, ids, cancellationToken);
        var paymentsByLoan = (await capitalPayments.ListByLoansAsync(tenantId, ids, cancellationToken)).ToLookup(payment => payment.LoanId);

        return loans.Select(loan => ToLoanDto(
            loan,
            customerRefs.GetValueOrDefault(loan.CustomerId),
            assignees,
            pendingByLoan.GetValueOrDefault(loan.Id) ?? new PendingTotal(0, 0m),
            LoanAccrual.ToDate(loan, paymentsByLoan[loan.Id], today),
            today)).ToList();
    }

    /// <summary>
    /// Por qué no se ofrece abonar; <c>null</c> si se puede. Primero «sin capital», luego «cuotas pendientes», y por último un período
    /// ya cerrado que todavía no es cuota (el worker lo resuelve en minutos).
    /// </summary>
    private static string? CapitalPaymentBlockOf(Loan loan, int pendingCount, DateOnly today)
    {
        if (loan.OutstandingPrincipal == 0m)
        {
            return nameof(CapitalPaymentBlock.NoCapital);
        }

        if (pendingCount > 0)
        {
            return nameof(CapitalPaymentBlock.PendingInstallments);
        }

        return HasUngeneratedClosedPeriod(loan, today) ? nameof(CapitalPaymentBlock.PeriodClosing) : null;
    }

    private static bool HasUngeneratedClosedPeriod(Loan loan, DateOnly today) =>
        loan.NextPeriodToGenerate() is { } next && next.End < today;

    private async Task<IReadOnlyDictionary<Guid, LoansMemberRef>> FindMembersAsync(
        Guid tenantId, IEnumerable<Guid> memberIds, CancellationToken cancellationToken) =>
        (await members.FindAsync(tenantId, memberIds.Distinct().ToArray(), cancellationToken)).ToDictionary(member => member.MemberId);

    private static LoanDto ToLoanDto(
        Loan loan,
        LoansCustomerRef? customer,
        IReadOnlyDictionary<Guid, LoansMemberRef> assignees,
        PendingTotal pending,
        LoanAccrualResult? accrual,
        DateOnly today) => new(
        loan.Id.Value,
        customer is null
            ? new LoanCustomerDto(loan.CustomerId, UnknownName, null, null)
            : new LoanCustomerDto(customer.Id, customer.Name, customer.PhoneE164, customer.Email),
        loan.Name,
        loan.Type.ToString(),
        loan.PrincipalAmount,
        loan.OutstandingPrincipal,
        loan.Currency,
        loan.MonthlyRatePercent,
        loan.DisbursedOn,
        loan.CutoffRule.ToString(),
        loan.AssigneeMemberId is { } assigneeId ? ToMemberDto(assigneeId, assignees.GetValueOrDefault(assigneeId)) : null,
        loan.Note,
        loan.Status.ToString(),
        pending.Count,
        pending.Amount,
        accrual?.Amount ?? 0m,
        loan.LastGeneratedPeriodEnd,
        loan.NextCutoffDate(today),
        loan.Version,
        loan.CreatedAt,
        loan.UpdatedAt);

    internal static LoanMemberDto ToMemberDto(Guid memberId, LoansMemberRef? member) =>
        member is null
            ? new LoanMemberDto(memberId, UnknownName, false)
            : new LoanMemberDto(member.MemberId, member.DisplayName, member.IsActive);

    private static LoanSegmentDto ToSegmentDto(InterestSegment segment) =>
        new(segment.Start, segment.End, segment.Principal, segment.Days, segment.Interest);

    private static LoanInstallmentDto ToInstallmentDto(LoanInstallment installment, IReadOnlyDictionary<Guid, LoansMemberRef> membersById) => new(
        installment.Id,
        installment.PeriodStart,
        installment.PeriodEnd,
        installment.PeriodDays,
        installment.Amount,
        installment.Currency,
        installment.Status.ToString(),
        installment.IsImported,
        installment.PaidOn is { } paidOn
            ? new LoanPaymentDto(
                paidOn,
                installment.PaymentNote,
                installment.PaidByMemberId is { } paidBy ? ToMemberDto(paidBy, membersById.GetValueOrDefault(paidBy)) : null,
                installment.PaidAt ?? default)
            : null,
        installment.Segments.Select(segment => new LoanSegmentDto(segment.StartOn, segment.EndOn, segment.Principal, segment.Days, segment.Interest)).ToList());

    private static LoanCapitalPaymentDto ToCapitalPaymentDto(LoanCapitalPayment payment) => new(
        payment.Id,
        payment.Amount,
        payment.PaidOn,
        payment.Note,
        payment.BalanceAfter,
        payment.VoidedAt,
        payment.VoidReason,
        payment.CreatedAt);

    private enum CapitalPaymentBlock
    {
        PendingInstallments,
        PeriodClosing,
        NoCapital,
    }
}
