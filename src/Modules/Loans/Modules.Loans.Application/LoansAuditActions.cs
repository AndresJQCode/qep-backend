namespace Modules.Loans.Application;

/// <summary>Las acciones que el módulo publica a auditoría (spec de préstamos, 2026-10-31).</summary>
public static class LoansAuditActions
{
    public const string LoanCreated = "loans.loan.created";

    public const string LoanUpdated = "loans.loan.updated";

    public const string LoanDeleted = "loans.loan.deleted";

    public const string InstallmentPaid = "loans.installment.paid";

    public const string InstallmentPaymentUpdated = "loans.installment.payment_updated";

    public const string InstallmentPaymentRemoved = "loans.installment.payment_removed";

    public const string CapitalPaymentCreated = "loans.capital_payment.created";

    public const string CapitalPaymentVoided = "loans.capital_payment.voided";

    /// <summary>Tipos de recurso (<c>resourceType</c>) de esas acciones.</summary>
    public static class Resources
    {
        public const string Loan = "loan";

        public const string Installment = "installment";

        public const string CapitalPayment = "capital_payment";
    }
}