namespace Modules.Quotations.Domain;

/// <summary>De dónde sale la identidad de a quien se le factura un pedido.</summary>
public enum BilledPartySource
{
    FinalConsumer,
    BillingParty,
    Customer
}

/// <summary>
/// La precedencia de «a quién se factura», en un solo lugar (spec 2026-10-26): consumidor final, la
/// fila de facturación propia o la ficha vigente del cliente. La usan la aprobación del pedido
/// (exige el tipo de persona de esa identidad) y el Excel de pedidos (escribe su documento), para
/// que las dos lean la misma persona y no diverjan.
/// </summary>
public static class OrderBilledParty
{
    /// <summary>Una fila de facturación cuenta sólo si trae nombre (Desvío 1): una fila sin nombre es
    /// un residuo, no una persona distinta del cliente.</summary>
    public static BilledPartySource SourceFor(bool billsToFinalConsumer, OrderParty? billing)
    {
        if (billsToFinalConsumer)
        {
            return BilledPartySource.FinalConsumer;
        }

        return billing?.Name is { Length: > 0 }
            ? BilledPartySource.BillingParty
            : BilledPartySource.Customer;
    }
}
