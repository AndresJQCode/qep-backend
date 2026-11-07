namespace Modules.Store.Domain;

/// <summary>La cuenta que cobra una moneda; su moneda debe ser la de la entrada que la contiene.</summary>
public sealed record StoreBillingAccount(Guid CompanyId, string BankName, string AccountNumber, string Currency);
