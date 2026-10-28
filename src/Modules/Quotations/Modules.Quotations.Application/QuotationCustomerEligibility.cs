using System.Diagnostics.CodeAnalysis;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// US-1/US-18: no se cotiza a un cliente inexistente, sin CUC, inactivo o con la ficha incompleta. Mismo criterio que
/// <c>ProductImageResolver</c> en Catalog — sin FK real que respalde la referencia (es blanda,
/// hacia otro módulo), esta comprobación es la única red.
/// </summary>
internal static class QuotationCustomerEligibility
{
    // [NotNull] documenta para el analisis de nulabilidad lo que el metodo ya garantiza en
    // runtime: si vuelve sin tirar, `customer` no es null — sin esto, cualquier llamador que
    // lea un campo de `customer` despues de `Ensure` (CreateQuotation.cs necesita
    // WithRetention/VatSurplus para el snapshot de totales) se topa con CS8602.
    public static void Ensure(
        [NotNull] QuotationCustomerRef? customer, Guid tenantId, Guid clientId)
    {
        // Mismo código para "no existe" y "es de otro tenant": distinguirlos le confirmaría al
        // llamador que el id existe en otro tenant, que es justo lo que la frontera esconde.
        if (customer is null || customer.TenantId != tenantId)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.client_not_found",
                $"Client '{clientId}' was not found in this tenant.");
        }

        // Spec 2026-10-10 §6.3 (D-A4): antes que client_cuc_missing. Un incompleto tampoco tiene CUC, y sin este
        // orden la pantalla diría «falta el CUC» en vez de «completa la ficha». Cubre crear, cambiar cliente, enviar y
        // convertir en pedido: los cuatro llamadores de Ensure.
        if (!customer.IsComplete)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.client_incomplete",
                "The client record is incomplete; complete it before quoting or selling.");
        }

        if (string.IsNullOrWhiteSpace(customer.Cuc))
        {
            throw new QuotationsDomainException(
                "quotation.quotation.client_cuc_missing",
                "The client does not have a CUC assigned.");
        }

        if (!customer.IsActive)
        {
            throw new QuotationsDomainException(
                "quotation.quotation.client_inactive",
                "An inactive client cannot be quoted.");
        }
    }
}
