using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Pipeline.Application;

/// <summary>Spec 2026-10-13 §4.5: lo que traen <c>quotations.quotation.sent.v1</c> y <c>…converted.v1</c> (este último con el pedido).</summary>
public sealed record QuotationFact(
    Guid TenantId, Guid QuotationId, string QuotationNumber, Guid ClientId, Guid AdvisorMemberId, decimal Total, string Currency, Guid? OrderId);

/// <summary>§4.5: lo que trae <c>quotations.order.cancelled.v1</c>.</summary>
public sealed record OrderCancelledFact(Guid TenantId, Guid OrderId, Guid QuotationId);

/// <summary>
/// Spec 2026-10-13 §4.4, D17, D18 y D25–D28: lo que cada evento de Quotations le hace al negocio, con actor <c>System</c>. No
/// guarda: el worker guarda con la unidad de trabajo, en el mismo SaveChanges que el inbox. El resultado no depende del orden de
/// llegada (converted aplica primero las reglas de enlace de sent, D25) y un evento repetido no duplica nada. Una principal
/// anulada o vencida no dispara nada (D28): Quotations no emite evento por eso.
/// </summary>
public sealed class QuotationEventApplier(
    ITenantModules tenantModules,
    IPipelineProvisioner provisioner,
    IDealRepository deals,
    IDealQuotationRepository quotations,
    IPipelineAuditRecorder audit)
{
    /// <returns><c>false</c> si el tenant no tiene la capacidad (P12): el mensaje se marca procesado sin efecto.</returns>
    public Task<bool> ApplySentAsync(QuotationFact fact, DateTimeOffset now, CancellationToken cancellationToken) =>
        ApplyQuotationAsync(fact, converted: false, now, cancellationToken);

    /// <returns>Ver <see cref="ApplySentAsync"/>.</returns>
    public Task<bool> ApplyConvertedAsync(QuotationFact fact, DateTimeOffset now, CancellationToken cancellationToken) =>
        ApplyQuotationAsync(fact, converted: true, now, cancellationToken);

    /// <summary>§4.4: si el negocio está ganado y su principal es la cotización de ese pedido, se reabre (D15).</summary>
    public async Task<bool> ApplyOrderCancelledAsync(OrderCancelledFact fact, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!await IsEnabledAsync(fact.TenantId, cancellationToken))
        {
            return false;
        }

        var snapshot = await quotations.FindAsync(fact.TenantId, fact.QuotationId, cancellationToken);
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
        if (deal.PrimaryQuotationId != fact.QuotationId || deal.StageId != pipeline.WonStage.Id)
        {
            return true;
        }

        var lastOpen = await deals.FindLastOpenStageIdAsync(fact.TenantId, deal.Id, cancellationToken);
        deal.Reopen(pipeline, lastOpen, DealActor.ForSystem, now);
        Record(fact.TenantId, PipelineAuditActions.DealReopened, deal.Id, now);
        return true;
    }

    private async Task<bool> ApplyQuotationAsync(QuotationFact fact, bool converted, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!await IsEnabledAsync(fact.TenantId, cancellationToken))
        {
            return false;
        }

        var pipeline = await provisioner.EnsureAsync(fact.TenantId, cancellationToken);

        // Paso 1: la foto.
        var snapshot = await quotations.FindAsync(fact.TenantId, fact.QuotationId, cancellationToken);
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
