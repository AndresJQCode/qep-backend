namespace Modules.Loans.Domain;

/// <summary>
/// Códigos estables de error del módulo (spec de préstamos, § Errores). El mapeo a HTTP vive en
/// <c>ApiExceptionHandler</c>; acá sólo el contrato.
/// </summary>
public static class LoansErrorCodes
{
    public const string LoanNotFound = "loans.loan.not_found";

    public const string InstallmentNotFound = "loans.installment.not_found";

    public const string CapitalPaymentNotFound = "loans.capital_payment.not_found";

    public const string CustomerNotFound = "loans.customer.not_found";

    public const string CustomerInactive = "loans.customer.inactive";

    public const string CurrencyUnknown = "loans.currency.unknown";

    public const string AssigneeInvalid = "loans.assignee.invalid";

    public const string LoanInvalid = "loans.loan.invalid";

    public const string LoanDisbursedInFuture = "loans.loan.disbursed_in_future";

    public const string LoanTermsLocked = "loans.loan.terms_locked";

    public const string LoanHasMovement = "loans.loan.has_movement";

    public const string InstallmentNotPending = "loans.installment.not_pending";

    public const string InstallmentNotPaid = "loans.installment.not_paid";

    public const string PaymentDateInFuture = "loans.payment.date_in_future";

    public const string PaymentNoteTooLong = "loans.payment.note_too_long";

    public const string CapitalPaymentPendingInstallments = "loans.capital_payment.pending_installments";

    public const string CapitalPaymentExceedsBalance = "loans.capital_payment.exceeds_balance";

    public const string CapitalPaymentDateOutOfPeriod = "loans.capital_payment.date_out_of_period";

    public const string CapitalPaymentAlreadyVoided = "loans.capital_payment.already_voided";

    public const string CapitalPaymentNotLatest = "loans.capital_payment.not_latest";

    public const string CapitalPaymentInstallmentGenerated = "loans.capital_payment.installment_generated";
}
