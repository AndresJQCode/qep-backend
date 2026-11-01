using BuildingBlocks.Application;
using Modules.Loans.Domain;

namespace Modules.Loans.Application;

// Las búsquedas siempre están acotadas al tenant del llamador, así que «no encontrado» significa «no está entre tus
// préstamos». Uno de otro tenant no llega hasta acá: la autorización lo frena antes con 403.
internal static class LoansNotFound
{
    public static ResourceNotFoundException Loan(Guid loanId) =>
        new(LoansErrorCodes.LoanNotFound, $"Loan '{loanId}' was not found.");

    public static ResourceNotFoundException Installment(Guid installmentId) =>
        new(LoansErrorCodes.InstallmentNotFound, $"Loan installment '{installmentId}' was not found.");

    public static ResourceNotFoundException CapitalPayment(Guid capitalPaymentId) =>
        new(LoansErrorCodes.CapitalPaymentNotFound, $"Capital payment '{capitalPaymentId}' was not found.");
}