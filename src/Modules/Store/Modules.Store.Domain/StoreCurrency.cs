namespace Modules.Store.Domain;

/// <summary>Una moneda en la que vende la tienda y la cuenta que la cobra. Nunca se convierte entre monedas.</summary>
public sealed record StoreCurrency(string Currency, StoreBillingAccount BillingAccount);
