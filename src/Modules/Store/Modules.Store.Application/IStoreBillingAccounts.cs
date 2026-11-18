using Modules.Store.Domain;

namespace Modules.Store.Application;

public interface IStoreBillingAccounts
{
    /// <summary>
    /// Las cuentas de las empresas activas del tenant, por nombre de empresa y luego por banco; filtradas por moneda si se pide.
    /// Cada empresa activa se ofrece además una vez sin cuenta (banco, número y moneda en null, spec 2026-11-17 §4.3), y esa
    /// fila sale con cualquier filtro de moneda: sin cuenta, la moneda la pone la entrada de la tienda.
    /// </summary>
    Task<IReadOnlyList<StoreBillingAccountOption>> ListAsync(
        Guid tenantId, string? currency, CancellationToken cancellationToken);

    /// <summary>
    /// Comprueba, con la misma regla con que un pedido resuelve su cuenta, que la cuenta es de esa empresa y de ese tenant, y la devuelve
    /// con los valores guardados por la empresa. Una cuenta que no pasa lanza los códigos <c>quotation.billing.*</c>. Banco y número
    /// nulos los dos sólo comprueban la empresa.
    /// </summary>
    Task<StoreBillingAccount> ValidateAsync(
        Guid tenantId,
        Guid companyId,
        string? bankName,
        string? accountNumber,
        string currency,
        CancellationToken cancellationToken);
}

/// <summary>Banco, número y moneda en null es la opción «empresa sin cuenta».</summary>
public sealed record StoreBillingAccountOption(
    Guid CompanyId, string CompanyName, string? BankName, string? AccountNumber, string? Currency);
