using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-10-13 §4.4, D17, D18 y D25–D28: lo que cada evento de Quotations le hace al negocio. El resultado no
/// depende del orden de llegada (D25) y un evento repetido no duplica nada.</summary>
public sealed class QuotationEventApplierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Later = ApplierBed.Now.AddHours(1);

    [Fact]
    public async Task ASentWithoutOpenDealsCreatesAQuotationDealInTheQuotationStage()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();

        Assert.True(await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId, 1_000m), Later, Ct));

        var deal = Assert.Single(bed.Deals.Items);
        Assert.Equal((DealSource.Quotation, bed.AdvisorId, "COT-0001", clientId), (deal.Source, deal.OwnerMemberId, deal.Title, deal.CustomerId));
        Assert.Equal((bed.Stage("Cotizado").Id, (Guid?)quotationId, (decimal?)1_000m, "COP"), (deal.StageId, deal.PrimaryQuotationId, deal.Amount, deal.Currency));
        Assert.Equal((Guid?)deal.Id, Assert.Single(bed.Quotations.Items).DealId);
        Assert.Equal(
            [PipelineAuditActions.DealCreated, PipelineAuditActions.QuotationLinked, PipelineAuditActions.PrimaryQuotationSet, PipelineAuditActions.DealMoved],
            bed.Audit.Entries.Select(entry => entry.Action));
        Assert.All(bed.Audit.Entries, entry => Assert.True(entry.BySystem));
        bed.Deals.Flush();
        Assert.All(bed.Deals.History, row => Assert.Equal(ActorKind.System, row.ActorKind));
    }

    [Fact]
    public async Task ASentWithExactlyOneOpenDealLinksAndAdvancesIt()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var deal = bed.OpenDeal(clientId);
        var quotationId = Guid.CreateVersion7();

        await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId, 2_000m, "USD"), Later, Ct);

        Assert.Single(bed.Deals.Items);
        Assert.Equal((bed.Stage("Cotizado").Id, (Guid?)quotationId, (decimal?)2_000m, "USD"), (deal.StageId, deal.PrimaryQuotationId, deal.Amount, deal.Currency));
    }

    [Fact]
    public async Task ASentWithSeveralOpenDealsLeavesItUnlinked()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var first = bed.OpenDeal(clientId, "Uno");
        var second = bed.OpenDeal(clientId, "Dos");

        await bed.Applier.ApplySentAsync(bed.Fact(clientId, Guid.CreateVersion7()), Later, Ct);

        Assert.Null(Assert.Single(bed.Quotations.Items).DealId);
        Assert.Equal((1L, 1L), (first.Version, second.Version));
        Assert.Empty(bed.Audit.Entries);
    }

    [Fact]
    public async Task AnAlreadyLinkedQuotationKeepsItsDealEvenWithOtherOpenDeals()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId, 1_000m), Later, Ct);
        var linked = Assert.Single(bed.Deals.Items);
        bed.OpenDeal(clientId, "Otro");

        await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId, 1_200m), Later.AddHours(1), Ct);

        Assert.Equal((Guid?)linked.Id, Assert.Single(bed.Quotations.Items).DealId);
        Assert.Equal((decimal?)1_200m, linked.Amount);
    }

    // D17: la máquina nunca le deshace al asesor una decisión.
    [Fact]
    public async Task AnAdvanceNeverGoesBackwards()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var deal = bed.OpenDeal(clientId);
        deal.MoveTo(bed.Pipeline, bed.Stage("Negociación").Id, DealActor.ForUser(bed.AdvisorId), ApplierBed.Now);

        await bed.Applier.ApplySentAsync(bed.Fact(clientId, Guid.CreateVersion7()), Later, Ct);

        Assert.Equal(bed.Stage("Negociación").Id, deal.StageId);
    }

    // D18 y D26: el monto sigue a la principal en el reenvío; otra cotización del mismo negocio no lo mueve.
    [Fact]
    public async Task OnlyThePrimaryMovesTheAmount()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var primary = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, primary, 1_000m), Later, Ct);
        var deal = Assert.Single(bed.Deals.Items);

        await bed.Applier.ApplySentAsync(bed.Fact(clientId, Guid.CreateVersion7(), 9_999m), Later, Ct);
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, primary, 1_300m), Later, Ct);

        Assert.Equal(((Guid?)primary, (decimal?)1_300m), (deal.PrimaryQuotationId, deal.Amount));
        Assert.Equal(2, bed.Quotations.Items.Count(quotation => quotation.DealId == deal.Id));
    }

    [Fact]
    public async Task AConvertedPrimaryWinsTheDeal()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId, 1_000m), Later, Ct);

        await bed.Applier.ApplyConvertedAsync(bed.Fact(clientId, quotationId, 1_100m, orderId: Guid.CreateVersion7()), Later.AddDays(1), Ct);

        var deal = Assert.Single(bed.Deals.Items);
        Assert.Equal((bed.Pipeline.WonStage.Id, (decimal?)1_100m), (deal.StageId, deal.Amount));
        Assert.Equal(DealQuotationStatus.Converted, Assert.Single(bed.Quotations.Items).Status);
    }

    // Review Focus 1 y D25: convertida desde Draft, o el sent esperando su reintento.
    [Fact]
    public async Task ConvertedWithoutSentCreatesTheDealAndWinsIt()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();

        await bed.Applier.ApplyConvertedAsync(bed.Fact(clientId, quotationId, 1_000m, orderId: Guid.CreateVersion7()), Later, Ct);

        var deal = Assert.Single(bed.Deals.Items);
        Assert.Equal((bed.Pipeline.WonStage.Id, (Guid?)quotationId, true), (deal.StageId, deal.PrimaryQuotationId, deal.IsClosed));
        bed.Deals.Flush();
        Assert.Equal([bed.Pipeline.FirstOpenStage.Id, bed.Pipeline.WonStage.Id], bed.Deals.History.Select(row => row.ToStageId));
    }

    [Fact]
    public async Task ARepeatedConvertedChangesNothing()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        var fact = bed.Fact(clientId, quotationId, 1_000m, orderId: Guid.CreateVersion7());
        await bed.Applier.ApplyConvertedAsync(fact, Later, Ct);
        var deal = Assert.Single(bed.Deals.Items);
        var version = deal.Version;
        bed.Deals.Flush();

        await bed.Applier.ApplyConvertedAsync(fact, Later.AddMinutes(1), Ct);
        bed.Deals.Flush();

        Assert.Single(bed.Deals.Items);
        Assert.Equal(version, deal.Version);
        Assert.Equal(2, bed.Deals.History.Count);
    }

    // Un sent que llega después del converted (reintento) no toca nada: Converted es terminal para sent. Ni la etapa, ni el monto
    // del negocio ganado, ni el enlace, aunque el sent viejo traiga otro total u otro cliente.
    [Fact]
    public async Task ALateSentAfterTheConvertedChangesNothing()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var staleClientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        await bed.Applier.ApplyConvertedAsync(bed.Fact(clientId, quotationId, 1_000m, orderId: Guid.CreateVersion7()), Later, Ct);
        var deal = Assert.Single(bed.Deals.Items);
        var version = deal.Version;

        Assert.True(await bed.Applier.ApplySentAsync(bed.Fact(staleClientId, quotationId, 7_777m), Later.AddMinutes(1), Ct));

        Assert.Single(bed.Deals.Items);
        Assert.Equal((bed.Pipeline.WonStage.Id, (decimal?)1_000m, (Guid?)quotationId, version), (deal.StageId, deal.Amount, deal.PrimaryQuotationId, deal.Version));
        var snapshot = Assert.Single(bed.Quotations.Items);
        Assert.Equal((DealQuotationStatus.Converted, (Guid?)deal.Id, clientId), (snapshot.Status, snapshot.DealId, snapshot.CustomerId));
        Assert.DoesNotContain(bed.Audit.Entries, entry => entry.Action == PipelineAuditActions.QuotationUnlinked);
    }

    [Fact]
    public async Task AConvertedNonPrimaryDoesNotWin()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, Guid.CreateVersion7()), Later, Ct);
        var other = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, other), Later, Ct);

        await bed.Applier.ApplyConvertedAsync(bed.Fact(clientId, other, orderId: Guid.CreateVersion7()), Later.AddDays(1), Ct);

        Assert.False(Assert.Single(bed.Deals.Items).IsClosed);
    }

    // D27: la cotización cambió de cliente; el negocio viejo pierde la principal y conserva el monto.
    [Fact]
    public async Task AChangedClientUnlinksAndRelinksToTheNewCustomer()
    {
        var bed = new ApplierBed();
        var oldClient = Guid.CreateVersion7();
        var newClient = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(oldClient, quotationId, 1_000m), Later, Ct);
        var oldDeal = Assert.Single(bed.Deals.Items);

        await bed.Applier.ApplySentAsync(bed.Fact(newClient, quotationId, 1_500m), Later.AddDays(1), Ct);

        Assert.Equal(((Guid?)null, (decimal?)1_000m, false), (oldDeal.PrimaryQuotationId, oldDeal.Amount, oldDeal.IsClosed));
        var newDeal = Assert.Single(bed.Deals.Items, deal => deal.CustomerId == newClient);
        Assert.Equal(((Guid?)quotationId, (decimal?)1_500m), (newDeal.PrimaryQuotationId, newDeal.Amount));
        Assert.Equal((Guid?)newDeal.Id, Assert.Single(bed.Quotations.Items).DealId);
        Assert.Contains(bed.Audit.Entries, entry => entry.Action == PipelineAuditActions.QuotationUnlinked && entry.ResourceId == oldDeal.Id);
    }

    // §4.4: el pedido anulado reabre el negocio ganado por esa cotización, a la última etapa abierta (D15).
    [Fact]
    public async Task ACancelledOrderReopensTheDealWonByThatQuotation()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId), Later, Ct);
        await bed.Applier.ApplyConvertedAsync(bed.Fact(clientId, quotationId, orderId: orderId), Later.AddDays(1), Ct);
        bed.Deals.Flush();

        await bed.Applier.ApplyOrderCancelledAsync(new OrderCancelledFact(bed.TenantId, orderId, quotationId, "Quotation"), Later.AddDays(2), Ct);

        var deal = Assert.Single(bed.Deals.Items);
        Assert.Equal((bed.Stage("Cotizado").Id, false), (deal.StageId, deal.IsClosed));
        Assert.NotNull(Assert.Single(bed.Quotations.Items).OrderCancelledAt);
    }

    // Reintento o entrega desordenada: el converted llega DESPUÉS del order.cancelled. No borra la anulación ni vuelve a ganar el
    // negocio que ya se reabrió; si no, un pedido anulado dejaría el negocio ganado para siempre.
    [Fact]
    public async Task AConvertedArrivingAfterTheCancellationKeepsItAndDoesNotWinAgain()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        var converted = bed.Fact(clientId, quotationId, orderId: orderId);
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId), Later, Ct);
        await bed.Applier.ApplyConvertedAsync(converted, Later.AddDays(1), Ct);
        bed.Deals.Flush();
        await bed.Applier.ApplyOrderCancelledAsync(new OrderCancelledFact(bed.TenantId, orderId, quotationId, "Quotation"), Later.AddDays(2), Ct);
        var deal = Assert.Single(bed.Deals.Items);
        var snapshot = Assert.Single(bed.Quotations.Items);
        var cancelledAt = snapshot.OrderCancelledAt;
        Assert.NotNull(cancelledAt);
        Assert.False(deal.IsClosed);
        var version = deal.Version;

        await bed.Applier.ApplyConvertedAsync(converted, Later.AddDays(3), Ct);

        Assert.Equal(cancelledAt, snapshot.OrderCancelledAt);
        Assert.Equal((orderId, DealQuotationStatus.Converted), ((Guid)snapshot.OrderId!, snapshot.Status));
        Assert.Equal((false, version), (deal.IsClosed, deal.Version));
    }

    // Orden (a): el order.cancelled del pedido X llega ANTES que su converted. La anulación adopta X; el converted de X es "el mismo
    // pedido", no una conversión nueva: no borra la anulación ni gana el negocio por un pedido anulado.
    [Fact]
    public async Task ACancellationBeforeItsConvertedKeepsTheCancellationAndDoesNotWin()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId), Later, Ct);
        await bed.Applier.ApplyOrderCancelledAsync(new OrderCancelledFact(bed.TenantId, orderId, quotationId, "Quotation"), Later.AddDays(1), Ct);

        await bed.Applier.ApplyConvertedAsync(bed.Fact(clientId, quotationId, orderId: orderId), Later.AddDays(2), Ct);

        var deal = Assert.Single(bed.Deals.Items);
        var snapshot = Assert.Single(bed.Quotations.Items);
        Assert.Equal((false, (DateTimeOffset?)Later.AddDays(1)), (deal.IsClosed, snapshot.OrderCancelledAt));
        Assert.Equal((orderId, DealQuotationStatus.Converted), ((Guid)snapshot.OrderId!, snapshot.Status));
        Assert.DoesNotContain(bed.Audit.Entries, entry => entry.Action == PipelineAuditActions.DealWon);
    }

    // Orden (b): un order.cancelled viejo del pedido X llega cuando el vigente ya es Y. No marca la foto ni reabre el negocio.
    [Fact]
    public async Task AStaleCancellationOfAReplacedOrderChangesNothing()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var quotationId = Guid.CreateVersion7();
        var oldOrder = Guid.CreateVersion7();
        var currentOrder = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, quotationId), Later, Ct);
        await bed.Applier.ApplyConvertedAsync(bed.Fact(clientId, quotationId, orderId: oldOrder), Later.AddDays(1), Ct);
        await bed.Applier.ApplyConvertedAsync(bed.Fact(clientId, quotationId, orderId: currentOrder), Later.AddDays(2), Ct);
        bed.Deals.Flush();
        var deal = Assert.Single(bed.Deals.Items);
        var snapshot = Assert.Single(bed.Quotations.Items);
        Assert.Equal((bed.Pipeline.WonStage.Id, (Guid?)currentOrder), (deal.StageId, snapshot.OrderId));
        var version = deal.Version;

        await bed.Applier.ApplyOrderCancelledAsync(new OrderCancelledFact(bed.TenantId, oldOrder, quotationId, "Quotation"), Later.AddDays(3), Ct);

        Assert.Equal((bed.Pipeline.WonStage.Id, true, version), (deal.StageId, deal.IsClosed, deal.Version));
        Assert.Null(snapshot.OrderCancelledAt);
        Assert.DoesNotContain(bed.Audit.Entries, entry => entry.Action == PipelineAuditActions.DealReopened);
    }

    // §4.4: sólo reabre el pedido de la cotización que ganó (la principal). Otra cotización del MISMO negocio ganado, convertida y
    // con su pedido anulado, deja el negocio en Ganado y sólo marca su foto. La precondición se afirma: sin ella la prueba no
    // distinguiría un aplicador que mira la principal de uno que reabre cualquier negocio enlazado.
    [Fact]
    public async Task ACancelledOrderOfAQuotationThatDidNotWinReopensNothing()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var winner = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var otherOrder = Guid.CreateVersion7();
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, winner), Later, Ct);
        await bed.Applier.ApplySentAsync(bed.Fact(clientId, other), Later, Ct);
        await bed.Applier.ApplyConvertedAsync(bed.Fact(clientId, winner, orderId: Guid.CreateVersion7()), Later.AddDays(1), Ct);
        await bed.Applier.ApplyConvertedAsync(bed.Fact(clientId, other, orderId: otherOrder), Later.AddDays(1), Ct);
        var deal = Assert.Single(bed.Deals.Items);
        var otherSnapshot = Assert.Single(bed.Quotations.Items, quotation => quotation.QuotationId == other);
        Assert.Equal(((Guid?)winner, (Guid?)deal.Id, bed.Pipeline.WonStage.Id), (deal.PrimaryQuotationId, otherSnapshot.DealId, deal.StageId));
        var version = deal.Version;

        await bed.Applier.ApplyOrderCancelledAsync(new OrderCancelledFact(bed.TenantId, otherOrder, other, "Quotation"), Later.AddDays(2), Ct);

        Assert.Equal((bed.Pipeline.WonStage.Id, true, version), (deal.StageId, deal.IsClosed, deal.Version));
        Assert.NotNull(otherSnapshot.OrderCancelledAt);
        Assert.DoesNotContain(bed.Audit.Entries, entry => entry.Action == PipelineAuditActions.DealReopened);
    }

    // P12: sin la capacidad, procesado sin efecto.
    [Fact]
    public async Task ATenantWithoutTheCapabilityIsIgnored()
    {
        var bed = new ApplierBed();
        bed.Modules.PipelineEnabled = false;

        Assert.False(await bed.Applier.ApplySentAsync(bed.Fact(Guid.CreateVersion7(), Guid.CreateVersion7()), Later, Ct));

        Assert.Empty(bed.Deals.Items);
        Assert.Empty(bed.Quotations.Items);
    }

    [Fact]
    public async Task AnOrderWithoutQuotationAndNoOpenDealCreatesAWonDeal()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();

        Assert.True(await bed.Applier.ApplyOrderCreatedAsync(bed.OrderFact(clientId, orderId, 500m, "Shopify"), Later, Ct));

        var deal = Assert.Single(bed.Deals.Items);
        Assert.Equal((DealSource.Order, bed.AdvisorId, clientId, bed.Pipeline.WonStage.Id, (decimal?)500m), (deal.Source, deal.OwnerMemberId, deal.CustomerId, deal.StageId, deal.Amount));
        Assert.Equal((Guid?)deal.Id, Assert.Single(bed.Orders.Items).DealId);
        Assert.Equal([PipelineAuditActions.DealCreated, PipelineAuditActions.OrderLinked, PipelineAuditActions.DealWon], bed.Audit.Entries.Select(entry => entry.Action));
    }

    [Fact]
    public async Task AnOrderWithoutQuotationWinsTheOnlyOpenDeal()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var deal = bed.OpenDeal(clientId);

        await bed.Applier.ApplyOrderCreatedAsync(bed.OrderFact(clientId, Guid.CreateVersion7(), 700m), Later, Ct);

        Assert.Single(bed.Deals.Items);
        Assert.Equal(bed.Pipeline.WonStage.Id, deal.StageId);
    }

    [Fact]
    public async Task AnOrderWithAQuotationDoesNothing()
    {
        var bed = new ApplierBed();
        var fact = bed.OrderFact(Guid.CreateVersion7(), Guid.CreateVersion7(), 700m, "Quotation") with { QuotationId = Guid.CreateVersion7() };

        Assert.True(await bed.Applier.ApplyOrderCreatedAsync(fact, Later, Ct));

        Assert.Empty(bed.Deals.Items);
        Assert.Empty(bed.Orders.Items);
    }

    // Review Focus 5: un reintento del outbox no crea un segundo negocio.
    [Fact]
    public async Task ARepeatedOrderCreatedIsIdempotent()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var fact = bed.OrderFact(clientId, Guid.CreateVersion7(), 500m);
        await bed.Applier.ApplyOrderCreatedAsync(fact, Later, Ct);

        await bed.Applier.ApplyOrderCreatedAsync(fact, Later.AddMinutes(5), Ct);

        Assert.Single(bed.Deals.Items);
        Assert.Single(bed.Orders.Items);
    }

    [Fact]
    public async Task CancellingAnOrderWithoutQuotationReopensItsWonDeal()
    {
        var bed = new ApplierBed();
        var clientId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        await bed.Applier.ApplyOrderCreatedAsync(bed.OrderFact(clientId, orderId, 500m), Later, Ct);
        var deal = Assert.Single(bed.Deals.Items);

        await bed.Applier.ApplyOrderCancelledAsync(new OrderCancelledFact(bed.TenantId, orderId, null, "Manual"), Later.AddHours(1), Ct);

        Assert.NotEqual(bed.Pipeline.WonStage.Id, deal.StageId);
        Assert.NotNull(Assert.Single(bed.Orders.Items).CancelledAt);
    }
}
