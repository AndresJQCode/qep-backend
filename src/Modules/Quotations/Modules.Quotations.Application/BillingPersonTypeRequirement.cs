using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Spec 2026-10-26: con el ajuste <c>OnCustomerSave</c> del tenant, una fila de facturación con nombre propio exige
/// su tipo de persona, en los mismos lugares donde ya se exige su número (crear, guardar, enviar y convertir; ver
/// <see cref="Quotation.EnsureBillingIdentified"/>). Es una lectura cruzada (el ajuste vive en Tenancy), por eso va
/// aparte del dominio, que no conoce al tenant.
/// </summary>
/// <remarks>
/// El requisito efectivo ya implica un tenant colombiano, y la fila «a otros datos» toma el país del tenant. Facturar
/// con los datos del cliente no se exige acá: lo cubre el cliente al guardarse.
/// </remarks>
internal static class BillingPersonTypeRequirement
{
    public const string ErrorCode = "quotation.billing.person_type_required";
    public const string BillingPersonTypeKey = "Parties.Billing.PersonType";

    public static async Task EnsureForQuotationAsync(
        ITenantDirectory directory,
        Guid tenantId,
        Quotation quotation,
        CancellationToken cancellationToken)
    {
        // Lo barato primero: sin fila propia, con consumidor final o con el tipo ya dicho no hace falta leer el tenant.
        if (quotation.BillsToFinalConsumer
            || quotation.Billing is not { PersonType: null } billing
            || string.IsNullOrWhiteSpace(billing.Name))
        {
            return;
        }

        var settings = await directory.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken);
        if (settings?.EffectivePersonTypeRequirement != PersonTypeRequirement.OnCustomerSave)
        {
            return;
        }

        throw new QuotationsDomainException(
            ErrorCode,
            "The tenant requires the billing party's person type.",
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [BillingPersonTypeKey] = [ErrorCode]
            });
    }
}
