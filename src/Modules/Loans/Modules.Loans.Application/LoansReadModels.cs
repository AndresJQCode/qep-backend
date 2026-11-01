using Modules.Loans.Domain;

namespace Modules.Loans.Application;

/// <summary>Un listado de préstamos por estado. La búsqueda es por nombre o por los clientes que ya resolvió quien llama.</summary>
public sealed record LoanListFilter(
    Guid TenantId,
    LoanStatus Status,
    string? SearchTerm,
    IReadOnlyCollection<Guid>? SearchCustomerIds,
    int Page,
    int PageSize);

/// <summary>Las cuotas pendientes de todo el tenant, con la misma búsqueda que <see cref="LoanListFilter"/> (por el nombre del préstamo).</summary>
public sealed record PendingInstallmentFilter(
    Guid TenantId,
    string? SearchTerm,
    IReadOnlyCollection<Guid>? SearchCustomerIds,
    int Page,
    int PageSize);

/// <summary>Las cuotas pendientes de un préstamo; el monto es de una sola moneda, la del préstamo.</summary>
public sealed record PendingTotal(int Count, decimal Amount);

public sealed record CurrencyTotal(string Currency, decimal Amount);

public sealed record LoanTotals(
    int ActiveCount,
    int SettledCount,
    int PendingInstallmentCount,
    IReadOnlyList<CurrencyTotal> OutstandingByCurrency,
    IReadOnlyList<CurrencyTotal> PendingByCurrency);

public sealed record OldestInstallment(LoanId LoanId, DateOnly PeriodEnd, decimal Amount, string Currency);

public sealed record InstallmentTotals(
    int PendingCount,
    IReadOnlyList<CurrencyTotal> PendingByCurrency,
    OldestInstallment? Oldest);
