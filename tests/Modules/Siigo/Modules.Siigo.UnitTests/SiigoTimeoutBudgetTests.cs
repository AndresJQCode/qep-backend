using Modules.Siigo.Domain;
using Modules.Siigo.Infrastructure.Api;
using Modules.Siigo.Infrastructure.Batches;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// Spec 2026-11-24 §4: los plazos de cada llamada y los dos leases dependen unos de otros. Si subes un plazo y esta prueba falla,
/// ajusta también <see cref="SiigoInvoice.SendLease"/> y el lease del worker.
/// </summary>
public sealed class SiigoTimeoutBudgetTests
{
    [Fact]
    public void EveryCallOfAnOrderFitsInsideTheSendLeaseAndTheSendLeaseInsideTheWorkerLease()
    {
        // Lo más que pasa sin renovar un reclamo: autenticar, buscar el cliente, crearlo y autenticar de nuevo, la factura y la del flete.
        // Un reenvío por redondeo de los pagos renueva los reclamos que quedan.
        var worstBetweenClaims = (SiigoApiClient.DefaultTimeout * 4) + (SiigoApiClient.InvoiceCreateTimeout * 2);
        // El lease del worker no se renueva: tiene que cubrir el pedido entero, con cada factura mandada dos veces.
        var worstOrder = (SiigoApiClient.DefaultTimeout * 4) + (SiigoApiClient.InvoiceCreateTimeout * 4);

        Assert.True(
            worstBetweenClaims < SiigoInvoice.SendLease,
            $"{worstBetweenClaims} does not fit in the send lease ({SiigoInvoice.SendLease}).");
        Assert.True(
            SiigoInvoice.SendLease < SiigoInvoiceBatchWorker.Lease,
            $"The send lease ({SiigoInvoice.SendLease}) does not fit in the worker lease ({SiigoInvoiceBatchWorker.Lease}).");
        Assert.True(
            worstOrder < SiigoInvoiceBatchWorker.Lease,
            $"{worstOrder} does not fit in the worker lease ({SiigoInvoiceBatchWorker.Lease}).");
    }

    // Siigo recomienda 120 s o más para crear documentos; todo lo demás sigue en 30 s (spec 2026-11-24 §4).
    [Fact]
    public void TheProductionDeadlinesAreThirtyAndOneHundredTwentySeconds() =>
        Assert.Equal(
            new SiigoApiTimeouts(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)),
            SiigoApiTimeouts.Production);
}
