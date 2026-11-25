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
        // El peor caso de un pedido: autenticar, buscar el cliente, crearlo y autenticar de nuevo, la factura y la del flete.
        var worstCase = (SiigoApiClient.DefaultTimeout * 4) + (SiigoApiClient.InvoiceCreateTimeout * 2);

        Assert.True(worstCase < SiigoInvoice.SendLease, $"{worstCase} does not fit in the send lease ({SiigoInvoice.SendLease}).");
        Assert.True(
            SiigoInvoice.SendLease < SiigoInvoiceBatchWorker.Lease,
            $"The send lease ({SiigoInvoice.SendLease}) does not fit in the worker lease ({SiigoInvoiceBatchWorker.Lease}).");
    }

    // Siigo recomienda 120 s o más para crear documentos; todo lo demás sigue en 30 s (spec 2026-11-24 §4).
    [Fact]
    public void TheProductionDeadlinesAreThirtyAndOneHundredTwentySeconds() =>
        Assert.Equal(
            new SiigoApiTimeouts(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)),
            SiigoApiTimeouts.Production);
}
