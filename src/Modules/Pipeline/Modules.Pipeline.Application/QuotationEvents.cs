using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Pipeline.Application;

/// <summary>Spec 2026-10-13 §4.5: lo que traen <c>quotations.quotation.sent.v1</c> y <c>…converted.v1</c> (este último con el pedido).</summary>
public sealed record QuotationFact(
    Guid TenantId, Guid QuotationId, string QuotationNumber, Guid ClientId, Guid AdvisorMemberId, decimal Total, string Currency, Guid? OrderId);

/// <summary>Spec 2026-10-15 §7: lo que trae <c>quotations.order.created.v1</c>. <c>Source</c> es el origen del pedido por nombre;
/// <c>QuotationId</c> es <c>null</c> en un pedido manual o de una integración.</summary>
public sealed record OrderFact(
    Guid TenantId, Guid OrderId, string OrderNumber, Guid ClientId, Guid AdvisorMemberId, decimal Total, string Currency, string Source, Guid? QuotationId);

/// <summary>§4.5: lo que trae <c>quotations.order.cancelled.v1</c>. Sin cotización (spec 2026-10-15) <c>QuotationId</c> es <c>null</c>.</summary>
public sealed record OrderCancelledFact(Guid TenantId, Guid OrderId, Guid? QuotationId, string Source);

/// <summary>
/// Spec 2026-10-13 §4.4, D17, D18 y D25–D28: lo que cada evento de Quotations le hace al negocio, con actor <c>System</c>. No
/// guarda: el worker guarda con la unidad de trabajo, en el mismo SaveChanges que el inbox. El resultado no depende del orden de
/// llegada (converted aplica primero las reglas de enlace de sent, D25) y un evento repetido no duplica nada. Una principal
/// anulada o vencida no dispara nada (D28): Quotations no emite evento por eso. Converted es terminal para sent: un sent que llega
/// después de la conversión no toca la foto, el monto ni el enlace.
///
/// Spec 2026-10-24 §4 «lo que llega tarde» (desvío F16): un evento que salió antes de una fusión trae el id del fusionado. Los
/// dos hechos que nombran cliente (<see cref="QuotationFact"/> y <see cref="OrderFact"/>) se resuelven al que quedó
/// <b>antes</b> de usarlos: si no, el sent le abriría un negocio a un cliente que ya no aparece en ninguna lista, y la
/// comparación del paso 2 tomaría el negocio ya movido por «de otro cliente» y desenlazaría su cotización.
/// </summary>
public sealed class QuotationEventApplier(
    ITenantModules tenantModules,
    IPipelineProvisioner provisioner,
    IDealRepository deals,
    IDealQuotationRepository quotations,
    IDealOrderRepository orders,
    IPipelineAuditRecorder audit,
    IPipelineCustomerDirectory customers)
{
    /// <returns><c>false</c> si el tenant no tiene la capacidad (P12): el mensaje se marca procesado sin efecto.</returns>
    public Task<bool> ApplySentAsync(QuotationFact fact, DateTimeOffset now, CancellationToken cancellationToken) =>
        ApplyQuotationAsync(fact, converted: false, now, cancellationToken);

    /// <returns>Ver <see cref="ApplySentAsync"/>.</returns>
    public Task<bool> ApplyConvertedAsync(QuotationFact fact, DateTimeOffset now, CancellationToken cancellationToken) =>
        ApplyQuotationAsync(fact, converted: true, now, cancellationToken);

    /// <summary>
    /// Spec 2026-10-15 §7: un pedido sin cotización gana el único negocio abierto del cliente o, si no hay ninguno, crea uno
    /// ganado (<see cref="DealSource.Order"/>). Con varios abiertos la foto queda suelta (D-O8). Un pedido con cotización no hace
    /// nada: el <c>converted</c> de esa cotización ya ganó el negocio. Idempotente por <c>(tenant, pedido)</c>.
    /// </summary>
    public async Task<bool> ApplyOrderCreatedAsync(OrderFact fact, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.QuotationId is not null)
        {
            return true;
        }

        if (!await IsEnabledAsync(fact.TenantId, cancellationToken))
        {
            return false;
        }

        fact = fact with { ClientId = await customers.ResolveSurvivorAsync(fact.TenantId, fact.ClientId, cancellationToken) };
        var pipeline = await provisioner.EnsureAsync(fact.TenantId, cancellationToken);
        if (await orders.FindAsync(fact.TenantId, fact.OrderId, cancellationToken) is not null)
        {
            return true;
        }

        var snapshot = DealOrder.Create(fact.TenantId, fact.OrderId, fact.OrderNumber, fact.ClientId, fact.Total, fact.Currency, fact.Source, now);
        orders.Add(snapshot);
        var open = await deals.ListOpenByCustomerAsync(fact.TenantId, fact.ClientId, cancellationToken);
        if (open.Count > 1)
        {
            return true;
        }

        Deal deal;
        if (open.Count == 1)
        {
            deal = open[0];
        }
        else
        {
            deal = Deal.Create(fact.TenantId, pipeline, fact.ClientId, fact.AdvisorMemberId, fact.OrderNumber, fact.Total, fact.Currency, null,
                DealSource.Order, null, DealActor.ForSystem, now);
            deals.Add(deal);
            Record(fact.TenantId, PipelineAuditActions.DealCreated, deal.Id, now);
        }

        snapshot.LinkTo(deal.Id, now);
        Record(fact.TenantId, PipelineAuditActions.OrderLinked, deal.Id, now);

        // D18: con principal el monto sigue a la cotización; sin principal y sin monto, el del pedido es el mejor dato que hay.
        if (deal.PrimaryQuotationId is null && deal.Amount is null)
        {
            deal.Update(deal.Title, deal.OwnerMemberId, fact.Total, fact.Currency, deal.ExpectedCloseDate, now);
        }

        if (!deal.IsClosed)
        {
            deal.Win(pipeline, DealActor.ForSystem, now);
            Record(fact.TenantId, PipelineAuditActions.DealWon, deal.Id, now);
        }

        return true;
    }

    /// <summary>§4.4: si el negocio está ganado y su principal es la cotización de ese pedido, se reabre (D15). Sin cotización
    /// (spec 2026-10-15 §7), se reabre el negocio ganado al que quedó enlazada la foto del pedido.</summary>
    public async Task<bool> ApplyOrderCancelledAsync(OrderCancelledFact fact, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!await IsEnabledAsync(fact.TenantId, cancellationToken))
        {
            return false;
        }

        if (fact.QuotationId is not { } quotationId)
        {
            return await ApplyOrderWithoutQuotationCancelledAsync(fact, now, cancellationToken);
        }

        var snapshot = await quotations.FindAsync(fact.TenantId, quotationId, cancellationToken);
        if (snapshot is null)
        {
            return true;
        }

        // Un aviso de un pedido que ya no es el vigente de la cotización (llegó tarde) no marca la foto ni reabre nada.
        if (!snapshot.MarkOrderCancelled(fact.OrderId, now))
        {
            return true;
        }

        if (snapshot.DealId is not { } dealId || await deals.FindAsync(fact.TenantId, dealId, cancellationToken) is not { } deal)
        {
            return true;
        }

        var pipeline = await provisioner.EnsureAsync(fact.TenantId, cancellationToken);
        if (deal.PrimaryQuotationId != quotationId || deal.StageId != pipeline.WonStage.Id)
        {
            return true;
        }

        await ReopenAsync(fact.TenantId, pipeline, deal, now, cancellationToken);
        return true;
    }

    private async Task<bool> ApplyOrderWithoutQuotationCancelledAsync(OrderCancelledFact fact, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var snapshot = await orders.FindAsync(fact.TenantId, fact.OrderId, cancellationToken);
        if (snapshot is null || !snapshot.MarkCancelled(now))
        {
            return true;
        }

        if (snapshot.DealId is not { } dealId || await deals.FindAsync(fact.TenantId, dealId, cancellationToken) is not { } deal)
        {
            return true;
        }

        var pipeline = await provisioner.EnsureAsync(fact.TenantId, cancellationToken);
        if (deal.StageId == pipeline.WonStage.Id)
        {
            await ReopenAsync(fact.TenantId, pipeline, deal, now, cancellationToken);
        }

        return true;
    }

    private async Task ReopenAsync(Guid tenantId, SalesPipeline pipeline, Deal deal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lastOpen = await deals.FindLastOpenStageIdAsync(tenantId, deal.Id, cancellationToken);
        deal.Reopen(pipeline, lastOpen, DealActor.ForSystem, now);
        Record(tenantId, PipelineAuditActions.DealReopened, deal.Id, now);
    }

    private async Task<bool> ApplyQuotationAsync(QuotationFact fact, bool converted, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!await IsEnabledAsync(fact.TenantId, cancellationToken))
        {
            return false;
        }

        // Antes de la foto y del paso 2: con el id del fusionado, el negocio ya movido parecería de otro cliente.
        fact = fact with { ClientId = await customers.ResolveSurvivorAsync(fact.TenantId, fact.ClientId, cancellationToken) };
        var pipeline = await provisioner.EnsureAsync(fact.TenantId, cancellationToken);

        // Paso 1: la foto.
        var snapshot = await quotations.FindAsync(fact.TenantId, fact.QuotationId, cancellationToken);

        // Converted es terminal para sent: un sent tardío (reintento) trae un total o un cliente viejos y no puede pisar el monto
        // del negocio ganado ni desenlazar su principal.
        if (!converted && snapshot?.Status == DealQuotationStatus.Converted)
        {
            return true;
        }

        if (snapshot is null)
        {
            snapshot = DealQuotation.Create(fact.TenantId, fact.QuotationId, fact.QuotationNumber, fact.ClientId, fact.Total, fact.Currency, now);
            quotations.Add(snapshot);
        }

        snapshot.Refresh(fact.QuotationNumber, fact.ClientId, fact.Total, fact.Currency, converted, fact.OrderId, now);

        // Paso 2 (D27): enlazada a un negocio de otro cliente → se desenlaza y se sigue como si nunca lo hubiera estado.
        var deal = snapshot.DealId is { } linkedId ? await deals.FindAsync(fact.TenantId, linkedId, cancellationToken) : null;
        if (deal is not null && deal.CustomerId != fact.ClientId)
        {
            if (deal.PrimaryQuotationId == fact.QuotationId)
            {
                deal.ClearPrimary(now);
            }

            snapshot.Unlink(now);
            Record(fact.TenantId, PipelineAuditActions.QuotationUnlinked, deal.Id, now);
            deal = null;
        }

        // Paso 3: enlace. Con varios negocios abiertos del cliente queda suelta: el asesor elige desde el negocio.
        if (deal is null)
        {
            var open = await deals.ListOpenByCustomerAsync(fact.TenantId, fact.ClientId, cancellationToken);
            if (open.Count > 1)
            {
                return true;
            }

            if (open.Count == 1)
            {
                deal = open[0];
            }
            else
            {
                deal = Deal.Create(fact.TenantId, pipeline, fact.ClientId, fact.AdvisorMemberId, fact.QuotationNumber, fact.Total,
                    fact.Currency, null, DealSource.Quotation, null, DealActor.ForSystem, now);
                deals.Add(deal);
                Record(fact.TenantId, PipelineAuditActions.DealCreated, deal.Id, now);
            }

            snapshot.LinkTo(deal.Id, now);
            Record(fact.TenantId, PipelineAuditActions.QuotationLinked, deal.Id, now);
        }

        // Paso 4 (D17, D18, D26): la principal y el monto. El monto sólo se refresca acá.
        if (deal.PrimaryQuotationId is null)
        {
            deal.SetPrimaryQuotation(fact.QuotationId, fact.Total, fact.Currency, now);
            Record(fact.TenantId, PipelineAuditActions.PrimaryQuotationSet, deal.Id, now);
        }
        else if (deal.PrimaryQuotationId == fact.QuotationId)
        {
            deal.FollowPrimary(fact.Total, fact.Currency, now);
        }

        // Paso 5: sent avanza sólo hacia adelante (D17); converted de la principal de un abierto gana (D25), salvo que su pedido
        // ya llegó anulado (un converted repetido o desordenado no vuelve a ganar el negocio que el order.cancelled reabrió).
        if (!converted)
        {
            if (deal.AdvanceToQuotationStage(pipeline, DealActor.ForSystem, now))
            {
                Record(fact.TenantId, PipelineAuditActions.DealMoved, deal.Id, now);
            }
        }
        else if (deal.PrimaryQuotationId == fact.QuotationId && !deal.IsClosed && snapshot.OrderCancelledAt is null)
        {
            deal.Win(pipeline, DealActor.ForSystem, now);
            Record(fact.TenantId, PipelineAuditActions.DealWon, deal.Id, now);
        }

        return true;
    }

    private async Task<bool> IsEnabledAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await tenantModules.FindAsync(tenantId, cancellationToken) is { } modules && modules.IsEnabled(TenantModuleKeys.Pipeline);

    // §6.3: lo del worker no tiene una persona detrás: System y actor vacío (H15).
    private void Record(Guid tenantId, string action, Guid dealId, DateTimeOffset now) =>
        audit.Record(tenantId, Guid.Empty, true, action, PipelineAuditActions.DealResource, dealId, now);
}
