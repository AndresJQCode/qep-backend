using Modules.Store.Domain;

namespace Modules.Store.Application;

public interface IStoreBillingAccounts
{
    /// <summary>Las cuentas de las empresas activas del tenant, por nombre de empresa y luego por banco; filtradas por moneda si se pide.</summary>
    Task<IReadOnlyList<StoreBillingAccountOption>> ListAsync(
        Guid tenantId, string? currency, CancellationToken cancellationToken);

    /// <summary>
    /// Comprueba, con la misma regla con que un pedido resuelve su cuenta, que la cuenta es de esa empresa y de ese tenant, y la devuelve
    /// con los valores guardados por la empresa. Una cuenta que no pasa lanza los códigos <c>quotation.billing.*</c>.
    /// </summary>
    Task<StoreBillingAccount> ValidateAsync(
        Guid tenantId,
        Guid companyId,
        string bankName,
        string accountNumber,
        string currency,
        CancellationToken cancellationToken);
}

public sealed record StoreBillingAccountOption(
    Guid CompanyId, string CompanyName, string BankName, string AccountNumber, string Currency);
