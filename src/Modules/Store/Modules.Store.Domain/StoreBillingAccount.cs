namespace Modules.Store.Domain;

/// <summary>
/// La cuenta que cobra una moneda; su moneda debe ser la de la entrada que la contiene. Banco y número nulos los dos es una
/// empresa sin cuenta bancaria (spec 2026-11-17 §4.3): la empresa emite el pedido y la moneda sigue siendo la de la entrada.
/// </summary>
public sealed record StoreBillingAccount(Guid CompanyId, string? BankName, string? AccountNumber, string Currency);
