using FluentValidation;
using Modules.Loans.Domain;
using Modules.Tenancy.Application;

namespace Modules.Loans.Application;

/// <summary>Las reglas de un préstamo (spec de préstamos, § Cuerpos) que comparten el alta, la edición y las listas.</summary>
internal static class LoanRules
{
    /// <summary>
    /// El piso de <c>disbursedOn</c>: el alta genera una cuota por cada mes cerrado desde el desembolso, así que una fecha de hace
    /// siglos sería un request de miles de cuotas (mismo criterio que <c>ServiceRules.EarliestFirstDueDate</c>). El techo no
    /// está acá: lo juzga <see cref="Loan.Create"/> con la «hoy» del tenant (<c>loans.loan.disbursed_in_future</c>).
    /// </summary>
    public static readonly DateOnly EarliestDisbursement = new(2000, 1, 1);

    /// <summary>Precisión de <c>principal_amount</c> (<c>numeric(18,2)</c>).</summary>
    public const int MoneyPrecision = 18;

    public const int MoneyScale = 2;

    /// <summary>Precisión de <c>monthly_rate_percent</c> (<c>numeric(7,4)</c>).</summary>
    public const int RatePrecision = 7;

    public const int RateScale = 4;

    /// <summary>Normaliza el código del catálogo o falla con <c>loans.currency.unknown</c> (422).</summary>
    public static string NormalizeCurrency(string currency)
    {
        if (!Currencies.IsSupported(currency))
        {
            throw new LoansDomainException(LoansErrorCodes.CurrencyUnknown, $"Currency '{currency}' is not supported.");
        }

        return Currencies.Normalize(currency);
    }

    /// <summary>
    /// Sólo los dos nombres exactos del contrato: <c>Enum.TryParse</c> aceptaría «0», «endofmonth» o un número fuera del enum,
    /// que el cable nunca debe producir.
    /// </summary>
    public static bool TryParseCutoffRule(string? text, out LoanCutoffRule rule)
    {
        switch (text)
        {
            case nameof(LoanCutoffRule.EndOfMonth):
                rule = LoanCutoffRule.EndOfMonth;
                return true;
            case nameof(LoanCutoffRule.DisbursementDay):
                rule = LoanCutoffRule.DisbursementDay;
                return true;
            default:
                rule = default;
                return false;
        }
    }

    public static IRuleBuilderOptions<T, string?> MustBeACutoffRule<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(text => TryParseCutoffRule(text, out _)).WithMessage("CutoffRule must be EndOfMonth or DisbursementDay.");

    /// <summary>
    /// Positivo y dentro de <c>numeric(18,2)</c>: el dominio no mide la escala, así que más decimales se redondearían en silencio
    /// en la base y más dígitos reventarían el INSERT con un 500.
    /// </summary>
    public static IRuleBuilderOptions<T, decimal> MustBeAMoneyAmount<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.GreaterThan(0m).PrecisionScale(MoneyPrecision, MoneyScale, ignoreTrailingZeros: true);

    /// <summary>Mayor que 0, hasta 100 y dentro de <c>numeric(7,4)</c>: 1,5 y 1,5000 son la misma tasa, 1,23456 no cabe.</summary>
    public static IRuleBuilderOptions<T, decimal> MustBeAMonthlyRate<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.GreaterThan(0m).LessThanOrEqualTo(100m).PrecisionScale(RatePrecision, RateScale, ignoreTrailingZeros: true);

    public static IRuleBuilderOptions<T, DateOnly> MustBeAReasonableDisbursement<T>(this IRuleBuilder<T, DateOnly> rule) =>
        rule.Must(date => date >= EarliestDisbursement).WithMessage("The disbursement date cannot be before 2000-01-01.");

    public static IRuleBuilderOptions<T, string> MustBeALoanName<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(text => text?.Trim() is { Length: > 0 and <= Loan.NameMaxLength })
            .WithMessage($"The name must have between 1 and {Loan.NameMaxLength} characters.");

    /// <summary>La nota se mide recortada, como la guarda el dominio: «   » es sin nota, no una nota larga.</summary>
    public static IRuleBuilderOptions<T, string?> MustBeALoanNote<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(text => (text?.Trim().Length ?? 0) <= Loan.NoteMaxLength)
            .WithMessage($"The note cannot exceed {Loan.NoteMaxLength} characters.");

    /// <summary>Sin cuotas ni filas de abono (un abono anulado cuenta: su fila no se borra). Es lo que cierra las condiciones.</summary>
    public static bool HasMovement(IReadOnlyCollection<LoanInstallment> installments, IReadOnlyCollection<LoanCapitalPayment> payments) =>
        installments.Count > 0 || payments.Count > 0;

    /// <summary>Sin cuotas pagadas ni filas de abono (un abono anulado cuenta). Lo usan el borrado y el <c>canDelete</c> del detalle.</summary>
    public static bool CanDelete(IReadOnlyCollection<LoanInstallment> installments, IReadOnlyCollection<LoanCapitalPayment> payments) =>
        installments.All(row => row.Status != LoanInstallmentStatus.Paid) && payments.Count == 0;

    /// <summary>La moneda del cuerpo, normalizada, o la del tenant si no vino.</summary>
    public static async Task<string> ResolveCurrencyAsync(
        string? requested, Guid tenantId, ITenantDefaultCurrency tenantDefaultCurrency, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(requested)
            ? await tenantDefaultCurrency.GetAsync(tenantId, cancellationToken)
            : NormalizeCurrency(requested);

    /// <summary>
    /// El cliente existe en el tenant (uno de otro tenant llega como «no existe») y está activo: <c>loans.customer.not_found</c> y
    /// <c>loans.customer.inactive</c>, ambos 422 porque el cliente es un campo del cuerpo y no el recurso de la ruta.
    /// </summary>
    public static async Task EnsureCustomerAsync(
        ILoansCustomerLookup customers, Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await customers.FindAsync(tenantId, customerId, cancellationToken)
            ?? throw new LoansDomainException(LoansErrorCodes.CustomerNotFound, $"Customer '{customerId}' was not found.");
        if (!customer.IsActive)
        {
            throw new LoansDomainException(LoansErrorCodes.CustomerInactive, "An inactive customer cannot get a loan.");
        }
    }

    /// <summary>El encargado es una membresía activa con <c>loans.loan.manage</c>: el mismo conjunto que ofrece <c>GET /assignees</c>.</summary>
    public static async Task EnsureAssigneeAsync(
        ILoansMemberDirectory members, Guid tenantId, Guid? assigneeMemberId, CancellationToken cancellationToken)
    {
        if (assigneeMemberId is not { } id)
        {
            return;
        }

        var found = await members.FindAsync(tenantId, [id], cancellationToken);
        if (found.Count == 0 || !found[0].IsActive || !found[0].CanManage)
        {
            throw new LoansDomainException(
                LoansErrorCodes.AssigneeInvalid, "The assignee must be an active member who can manage loans.");
        }
    }
}
