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
/// </summary>
internal static class QuotationBillingAccountResolver
{
    public static async Task<QuotationBillingAccount?> ResolveAsync(
        IQuotationCompanyLookup lookup,
        Guid tenantId,
        QuotationBillingAccountRequest? request,
        CancellationToken cancellationToken)
    {
        // Null es "no se eligió cuenta" y es válido: un borrador nace así, y el PATCH reemplaza
        // el recurso entero, así que mandarlo en null la limpia.
        if (request is null) return null;

        var company = await lookup.FindAsync(tenantId, request.CompanyId, cancellationToken);

        // Mismo código para "no existe" y "es de otro tenant", igual que con el cliente:
        // distinguirlos confirmaría que el id existe en otro tenant.
        if (company is null)
        {
            throw new QuotationsDomainException(
                "quotation.billing.company_not_found",
                $"Company '{request.CompanyId}' was not found in this tenant.");
        }

        var matches = company.BankAccounts.Any(account =>
            string.Equals(account.BankName, request.BankName?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            account.AccountNumber == request.AccountNumber?.Trim() &&
            string.Equals(account.Currency, request.Currency?.Trim(), StringComparison.OrdinalIgnoreCase));

        if (!matches)
        {
            throw new QuotationsDomainException(
                "quotation.billing.account_not_found",
                "The bank account does not belong to the selected company.");
        }

        return new QuotationBillingAccount
        {
            CompanyId = company.Id,
            BankName = request.BankName,
            AccountNumber = request.AccountNumber,
            Currency = request.Currency
        };
    }
}
