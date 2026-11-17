using Modules.Quotations.Domain;

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
///
/// <b>Empresa sin cuenta</b> (spec 2026-11-17, R3): un request sin banco ni número arma la cuenta con la empresa y banco
/// y número nulos. Su moneda es <c>currencyWithoutAccount</c>, la que decide quien llama (la por defecto del tenant en un
/// documento nuevo, la del documento al editarlo, la de la tienda o la del pedido de origen); el <c>Currency</c> del
/// request se ignora.
/// </summary>
internal static class QuotationBillingAccountResolver
{
    /// <summary>
    /// La variante de las vistas previas (spec 2026-11-16, retenciones por empresa): sin cuenta pero con empresa, arma la
    /// empresa sin cuenta, como un request <c>(companyId, null, null, null)</c>. La cuenta, si viene, ya nombra la empresa
    /// y gana.
    /// </summary>
    public static Task<QuotationBillingAccount?> ResolveAsync(
        IQuotationCompanyLookup lookup,
        Guid tenantId,
        QuotationBillingAccountRequest? request,
        Guid? billingCompanyId,
        string currencyWithoutAccount,
        CancellationToken cancellationToken)
    {
        var effective = request
            ?? (billingCompanyId is { } companyId
                ? new QuotationBillingAccountRequest(companyId, null, null, null)
                : null);
        return ResolveAsync(lookup, tenantId, effective, currencyWithoutAccount, cancellationToken);
    }

    public static async Task<QuotationBillingAccount?> ResolveAsync(
        IQuotationCompanyLookup lookup,
        Guid tenantId,
        QuotationBillingAccountRequest? request,
        string currencyWithoutAccount,
        CancellationToken cancellationToken)
    {
        // Null es "no se eligió empresa" y es válido: un borrador nace así, y el PATCH reemplaza
        // el recurso entero, así que mandarlo en null la limpia.
        if (request is null) return null;

        var withoutAccount = string.IsNullOrWhiteSpace(request.BankName) && string.IsNullOrWhiteSpace(request.AccountNumber);

        // La comprobación vive en un solo lugar (QuotationBillingAccountVerifier), compartida con la tienda pública.
        var verified = await QuotationBillingAccountVerifier.VerifyAsync(
            lookup,
            tenantId,
            request.CompanyId,
            request.BankName,
            request.AccountNumber,
            withoutAccount ? currencyWithoutAccount : request.Currency,
            cancellationToken);

        return new QuotationBillingAccount
        {
            CompanyId = verified.CompanyId,
            BankName = withoutAccount ? null : request.BankName,
            AccountNumber = withoutAccount ? null : request.AccountNumber,
            Currency = verified.Currency,
            Country = verified.Country,
            CityId = verified.CityId,
            ChargesVat = verified.ChargesVat,
        };
    }
}
