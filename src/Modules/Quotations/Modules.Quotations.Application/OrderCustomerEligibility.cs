using System.Diagnostics.CodeAnalysis;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Espejo de <see cref="QuotationCustomerEligibility"/> con los códigos del pedido (spec 2026-10-15 §5.2).
/// <paramref name="allowIncomplete"/> va en <c>true</c> sólo para un pedido de origen externo (spec de Shopify,
/// «Solo deja pasar el pedido incompleto si no viene de cotización»): un pedido manual exige cliente completo y
/// con CUC, igual que la cotización. El orden de las reglas es el mismo: incompleto antes que sin CUC, para que
/// la pantalla diga «completa la ficha» y no «falta el CUC».
/// </summary>
internal static class OrderCustomerEligibility
{
    public static void Ensure(
        [NotNull] QuotationCustomerRef? customer, Guid tenantId, Guid clientId, bool allowIncomplete)
    {
        // Mismo código para "no existe" y "es de otro tenant": distinguirlos confirmaría que el id
        // existe en otro tenant.
        if (customer is null || customer.TenantId != tenantId)
        {
            throw new QuotationsDomainException(
                "order.order.client_not_found",
                $"Client '{clientId}' was not found in this tenant.");
        }

        if (!allowIncomplete && !customer.IsComplete)
        {
            throw new QuotationsDomainException(
                "order.order.client_incomplete",
                "The client record is incomplete; complete it before selling.");
        }

        if (!allowIncomplete && string.IsNullOrWhiteSpace(customer.Cuc))
        {
            throw new QuotationsDomainException(
                "order.order.client_cuc_missing",
                "The client does not have a CUC assigned.");
        }

        if (!customer.IsActive)
        {
            throw new QuotationsDomainException(
                "order.order.client_inactive",
                "An inactive client cannot be sold to.");
        }
    }
}
