namespace Modules.Quotations.Application;

/// <summary>
/// Puerto hacia Companies para dos cosas distintas y las dos necesarias: <b>verificar</b> que la
/// cuenta que llega en un request es realmente una de esa empresa antes de copiarla, y
/// <b>resolver</b> razón social y NIT al mostrar una cotización ya guardada.
///
/// Mismo criterio de aislamiento que <see cref="IQuotationCustomerLookup"/>: el adaptador vive en
/// <c>Bootstrapper</c>. Trae también las empresas inactivas — una cotización vieja puede haberse
/// emitido con una empresa que después se dio de baja, y su nombre sigue haciendo falta.
/// </summary>
public interface IQuotationCompanyLookup
{
    Task<QuotationCompanyRef?> FindAsync(
        Guid tenantId,
        Guid companyId,
        CancellationToken cancellationToken);
}

public sealed record QuotationCompanyRef(
    Guid Id,
    string Name,
    string TaxId,
    bool IsActive,
    IReadOnlyCollection<QuotationCompanyAccountRef> BankAccounts);

public sealed record QuotationCompanyAccountRef(
    string BankName,
    string AccountNumber,
    string Currency);
