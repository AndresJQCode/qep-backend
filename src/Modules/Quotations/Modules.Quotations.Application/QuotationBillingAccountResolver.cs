using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Convierte la cuenta que llega en un request en el value object que la cotización congela,
/// después de comprobar que esa cuenta es de verdad de esa empresa y de este tenant.
///
/// La comprobación no es ceremonia: el cuerpo del request lo escribe el cliente, y sin ella una
/// cotización podría salir en PDF diciendo "paguen a esta cuenta" con un número que nadie de la
/// empresa cargó nunca. Mismo criterio que <see cref="QuotationCustomerEligibility"/> — sin FK
/// real que respalde la referencia entre módulos, esta comprobación es la única red.
///
/// Compara con la misma clave con la que <c>CompanyBankAccount</c> detecta duplicados: banco sin
/// distinguir mayúsculas, número tal cual. Usar otra dejaría entrar como "cuenta distinta" lo que
/// la empresa considera repetido, o al revés.
/// </summary>
internal static class QuotationBillingAccountResolver
{
    /// <summary>
    /// The previews' variant (spec 2026-11-16 withholdings by company): with no account but a company, a
    /// <see cref="QuotationBillingAccount.IsPending"/> account of that company, so withholdings follow the company before
    /// the bank account is picked. The account, when present, already names the company and wins. Write paths never pass
    /// a company: they use the overload without it and keep requiring the full account.
    /// </summary>
    public static async Task<QuotationBillingAccount?> ResolveAsync(
        IQuotationCompanyLookup lookup,
        Guid tenantId,
        QuotationBillingAccountRequest? request,
        Guid? billingCompanyId,
        string defaultCurrency,
        CancellationToken cancellationToken)
    {
        if (request is not null || billingCompanyId is not { } companyId)
        {
            return await ResolveAsync(lookup, tenantId, request, cancellationToken);
        }

        ArgumentNullException.ThrowIfNull(lookup);
        var company = await lookup.FindAsync(tenantId, companyId, cancellationToken)
            // Same code and message as an account naming an unknown company: unknown and another tenant's look alike.
            ?? throw new QuotationsDomainException(
                QuotationBillingAccountVerifier.CompanyNotFoundCode,
                $"Company '{companyId}' was not found in this tenant.");

        // The currency the caller says the document has without an account (the tenant default, or an existing
        // document's own): choosing only a company never changes it, because the currency belongs to the missing account.
        return new QuotationBillingAccount
        {
            CompanyId = company.Id,
            BankName = string.Empty,
            AccountNumber = string.Empty,
            Currency = defaultCurrency,
            Country = company.Country,
            CityId = company.CityId,
            IsPending = true,
        };
    }

    public static async Task<QuotationBillingAccount?> ResolveAsync(
        IQuotationCompanyLookup lookup,
        Guid tenantId,
        QuotationBillingAccountRequest? request,
        CancellationToken cancellationToken)
    {
        // Null es "no se eligió cuenta" y es válido: un borrador nace así, y el PATCH reemplaza
        // el recurso entero, así que mandarlo en null la limpia.
        if (request is null) return null;

        // La comprobación vive en un solo lugar (QuotationBillingAccountVerifier), compartida con la tienda pública.
        var verified = await QuotationBillingAccountVerifier.VerifyAsync(
            lookup,
            tenantId,
            request.CompanyId,
            request.BankName,
            request.AccountNumber,
            request.Currency,
            cancellationToken);

        return new QuotationBillingAccount
        {
            CompanyId = verified.CompanyId,
            BankName = request.BankName,
            AccountNumber = request.AccountNumber,
            Currency = verified.Currency,
            Country = verified.Country,
            CityId = verified.CityId,
        };
    }
}
