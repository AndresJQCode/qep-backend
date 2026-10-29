namespace Modules.Billing.Application;

/// <summary>
/// De un teléfono como lo escribió la persona a E.164 con «+», con el país ISO alfa-2 como contexto
/// (<c>null</c> → <c>CO</c>). Puerto propio de Billing (precedente <c>ISupplierPhoneNormalizer</c>) para que
/// Application no referencie Customers; lo usa la importación (§3.2b, <c>phone_invalid</c>). Devuelve
/// <c>null</c> si no parsea o no es válido.
/// </summary>
public interface IBillingPhoneNormalizer
{
    string? ToE164(string? phone, string? country);
}
