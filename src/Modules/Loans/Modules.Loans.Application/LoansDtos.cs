namespace Modules.Loans.Application;

// Contrato HTTP de préstamos (plan 2026-10-31, § Contrato HTTP). Convención: los opcionales viajan como null, nunca ausentes;
// los enums van por el cable como texto (LoanStatus: Active | Settled, LoanType: InterestOnly, CutoffRule: EndOfMonth |
// DisbursementDay, InstallmentStatus: Pending | Paid, CapitalPaymentBlock: PendingInstallments | PeriodClosing | NoCapital).
// Dinero y tasa son decimal: el JSON los escribe como números, con hasta 2 y 4 decimales.

public sealed record LoanCustomerDto(Guid Id, string Name, string? PhoneE164, string? Email);

public sealed record LoanMemberDto(Guid MemberId, string DisplayName, bool IsActive);

/// <summary>Las listas van en orden de catálogo y sin monedas en cero.</summary>
public sealed record LoanCurrencyAmountDto(string Currency, decimal Amount);

public sealed record LoanSegmentDto(DateOnly Start, DateOnly End, decimal Principal, int Days, decimal Interest);

public sealed record LoanPaymentDto(DateOnly PaidOn, string? Note, LoanMemberDto? PaidBy, DateTimeOffset PaidAt);

/// <param name="Customer"><c>Name</c> es «Sin nombre» si el cliente ya no se resuelve.</param>
/// <param name="OutstandingPrincipal">Capital pendiente; <c>PrincipalAmount</c> es el desembolsado.</param>
/// <param name="PendingAmount">«Por cobrar»: la suma de las cuotas pendientes.</param>
/// <param name="AccruedToDate">Interés causado a hoy en el período abierto; 0 sin capital. Todavía no se debe.</param>
/// <param name="NextCutoffDate">
/// Último día del período que contiene hoy, según <c>CutoffRule</c>. Lo calcula el backend, nunca el frontend. <c>null</c> si el
/// préstamo está <c>Settled</c>, y también si está <c>Active</c> sin capital: ya no tiene más cortes.
/// </param>
/// <param name="Version">Va en <c>If-Match</c>.</param>
public sealed record LoanDto(
    Guid Id,
    LoanCustomerDto Customer,
    string Name,
    string Type,
    decimal PrincipalAmount,
    decimal OutstandingPrincipal,
    string Currency,
    decimal MonthlyRatePercent,
    DateOnly DisbursedOn,
    string CutoffRule,
    LoanMemberDto? Assignee,
    string? Note,
    string Status,
    int PendingCount,
    decimal PendingAmount,
    decimal AccruedToDate,
    DateOnly? LastGeneratedPeriodEnd,
    DateOnly? NextCutoffDate,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="PeriodStart">En un primer período parcial es el desembolso.</param>
/// <param name="PeriodEnd">En la cuota de saldar es la fecha del abono, y el último tramo termina el día anterior.</param>
/// <param name="PeriodDays">Divisor del cálculo: los días del período completo. <c>null</c> si <c>IsImported</c>.</param>
/// <param name="Amount">La suma de <c>Segments</c>, salvo <c>IsImported</c>.</param>
/// <param name="IsImported">Sin desglose: <c>Segments</c> viene vacío.</param>
/// <param name="Segments">En orden de fecha.</param>
public sealed record LoanInstallmentDto(
    Guid Id,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int? PeriodDays,
    decimal Amount,
    string Currency,
    string Status,
    bool IsImported,
    LoanPaymentDto? Payment,
    IReadOnlyList<LoanSegmentDto> Segments);

public sealed record LoanCapitalPaymentDto(
    Guid Id,
    decimal Amount,
    DateOnly PaidOn,
    string? Note,
    decimal BalanceAfter,
    DateTimeOffset? VoidedAt,
    string? VoidReason,
    DateTimeOffset CreatedAt);

/// <param name="AccruedAmount">Igual a <c>Loan.AccruedToDate</c>.</param>
public sealed record LoanCurrentPeriodDto(
    DateOnly Start,
    DateOnly End,
    int Days,
    decimal AccruedAmount,
    IReadOnlyList<LoanSegmentDto> Segments);

/// <summary>El rango válido de la fecha de un abono a capital.</summary>
public sealed record LoanPaymentWindowDto(DateOnly From, DateOnly To);

// Backend for Frontend: la pantalla no calcula si se puede abonar ni a qué abono se le puede anular. Lo decide el backend y lo
// manda en el detalle: capitalPaymentBlock, capitalPaymentWindow y voidableCapitalPaymentId. Un abono anulado cuenta como
// abono para CanEditTerms y CanDelete: su fila no se borra.

/// <param name="Installments">Del período más reciente al más viejo.</param>
/// <param name="CapitalPayments">Del más reciente al más viejo, anulados incluidos.</param>
/// <param name="CurrentPeriod"><c>null</c> sin capital.</param>
/// <param name="CapitalPaymentBlock">
/// Por qué no se ofrece abonar; <c>null</c> si se puede. <c>PeriodClosing</c>: hay un período cerrado sin cuota, se resuelve en minutos.
/// </param>
/// <param name="CapitalPaymentWindow">No nulo sólo si <c>CapitalPaymentBlock</c> es <c>null</c>.</param>
/// <param name="VoidableCapitalPaymentId">El único abono que hoy se puede anular.</param>
/// <param name="CanEditTerms">Sin cuotas ni abonos; si es <c>false</c>, sólo nombre, encargado y nota.</param>
/// <param name="CanDelete">Sin cuotas pagadas ni abonos.</param>
public sealed record LoanDetailDto(
    LoanDto Loan,
    IReadOnlyList<LoanInstallmentDto> Installments,
    IReadOnlyList<LoanCapitalPaymentDto> CapitalPayments,
    LoanCurrentPeriodDto? CurrentPeriod,
    string? CapitalPaymentBlock,
    LoanPaymentWindowDto? CapitalPaymentWindow,
    Guid? VoidableCapitalPaymentId,
    bool CanEditTerms,
    bool CanDelete);

/// <summary>Del tenant entero, sin filtros.</summary>
public sealed record LoanSummaryDto(
    int ActiveCount,
    int SettledCount,
    int PendingInstallmentCount,
    IReadOnlyList<LoanCurrencyAmountDto> OutstandingPrincipalByCurrency,
    IReadOnlyList<LoanCurrencyAmountDto> PendingAmountByCurrency,
    IReadOnlyList<LoanCurrencyAmountDto> AccruedToDateByCurrency);

public sealed record LoanPageDto(IReadOnlyList<LoanDto> Items, int Total, int Page, int PageSize, LoanSummaryDto Summary);

public sealed record LoanInstallmentRowDto(
    Guid Id,
    Guid LoanId,
    string LoanName,
    LoanCustomerDto Customer,
    LoanMemberDto? Assignee,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal Amount,
    string Currency,
    bool IsImported);

public sealed record LoanOldestInstallmentDto(DateOnly PeriodEnd, decimal Amount, string Currency, string CustomerName);

/// <summary>Del tenant entero, sin filtros.</summary>
public sealed record LoanInstallmentSummaryDto(
    int PendingCount,
    IReadOnlyList<LoanCurrencyAmountDto> PendingAmountByCurrency,
    LoanOldestInstallmentDto? Oldest);

public sealed record LoanInstallmentPageDto(
    IReadOnlyList<LoanInstallmentRowDto> Items,
    int Total,
    int Page,
    int PageSize,
    LoanInstallmentSummaryDto Summary);

public sealed record LoanFinalInstallmentDto(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int PeriodDays,
    decimal Amount,
    string Currency,
    IReadOnlyList<LoanSegmentDto> Segments);

/// <param name="Settles">El abono lleva el capital a cero.</param>
/// <param name="FinalInstallment"><c>null</c> si no salda o si no hay interés causado (saldar el primer día del período abierto, o un interés que redondea a 0,00).</param>
public sealed record LoanCapitalPaymentPreviewDto(
    decimal ResultingPrincipal,
    bool Settles,
    LoanFinalInstallmentDto? FinalInstallment);

public sealed record LoanAssigneesDto(IReadOnlyList<LoanMemberDto> Items);