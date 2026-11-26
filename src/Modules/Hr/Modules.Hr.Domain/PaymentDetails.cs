namespace Modules.Hr.Domain;

public sealed record PaymentDetails(
    PaymentMethod Method,
    string? BankName,
    BankAccountType? BankAccountType,
    string? BankAccountNumber);
