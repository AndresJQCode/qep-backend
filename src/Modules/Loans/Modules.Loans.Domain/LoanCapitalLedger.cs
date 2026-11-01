namespace Modules.Loans.Domain;

/// <summary>El libro de abonos: qué cuenta, en qué orden y cuánto capital había antes de una fecha.</summary>
public static class LoanCapitalLedger
{
    /// <summary>
    /// Los abonos vigentes, por fecha del abono y no por orden de carga: uno cargado tarde con fecha vieja
    /// queda antes. Desempata <c>CreatedAt</c> y luego <c>Id</c> para que el orden sea siempre el mismo.
    /// </summary>
    public static IReadOnlyList<LoanCapitalPayment> InOrder(IEnumerable<LoanCapitalPayment> payments) =>
        payments
            .Where(payment => payment.VoidedAt is null)
            .OrderBy(payment => payment.PaidOn)
            .ThenBy(payment => payment.CreatedAt)
            .ThenBy(payment => payment.Id)
            .ToList();

    /// <summary>El capital al empezar <paramref name="date"/>: el saldo del último abono anterior, o el capital prestado.</summary>
    public static decimal OpeningBalance(Loan loan, IEnumerable<LoanCapitalPayment> payments, DateOnly date) =>
        InOrder(payments).LastOrDefault(payment => payment.PaidOn < date)?.BalanceAfter ?? loan.PrincipalAmount;

    /// <summary>Los cambios de capital desde <paramref name="date"/> inclusive, listos para el cálculo de tramos.</summary>
    public static IReadOnlyList<CapitalChange> ChangesFrom(IEnumerable<LoanCapitalPayment> payments, DateOnly date) =>
        InOrder(payments)
            .Where(payment => payment.PaidOn >= date)
            .Select(payment => new CapitalChange(payment.PaidOn, payment.BalanceAfter))
            .ToList();
}
