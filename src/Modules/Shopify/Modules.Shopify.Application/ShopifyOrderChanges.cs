using BuildingBlocks.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §6.3 (ruling S17): pago y cancelación de un pedido de Shopify que ya estaba en el espejo. La ingesta la llama
/// con el estado anterior de la fila, antes del guardado que persiste el snapshot; lo que escribe en la fila
/// (<c>last_error</c>, <c>MarkCancelled</c>, <c>MarkIgnored</c>) entra en ese mismo commit.
/// <list type="bullet">
/// <item><b>Importada y cancelada en Shopify:</b> se anula en QEP a nombre del asesor de la tienda («Cancelado en Shopify:
/// {cancel_reason}») y la fila pasa a <c>Cancelled</c>. Ya facturada: <c>Cancelled</c> con <c>invoiced_before_cancel</c> para
/// anular a mano. Otro rechazo: queda importada con el código en <c>last_error</c> (decisión pendiente del owner).</item>
/// <item><b>Importada con otro <c>financial_status</c>:</b> se sincroniza el pago; un rechazo (<c>not_pending</c>,
/// <c>payment_has_proofs</c>) va a <c>last_error</c>; <c>refunded</c>, <c>partially_refunded</c> y <c>voided</c> no dejan
/// rastro (ruling S19).</item>
/// <item><b>En espera y cancelada:</b> <c>Ignored</c>, salvo que el pedido ya exista en QEP con su clave externa (el worker cayó
/// entre crearlo y marcar la fila): entonces se marca importada y se anula como cualquier otra.</item>
/// <item><b><c>Cancelled</c> e <c>Ignored</c></b> son terminales (ruling S13): nada.</item>
/// </list>
/// <para><b>Idempotente.</b> Se repite en el bucle de concurrencia de la ingesta (hasta
/// <see cref="ShopifyOrderIngestion.MaxConcurrencyAttempts"/> veces por entrega) y cuando el guardado del espejo falla después de
/// actuar en Quotations: la anulación repetida responde <c>order.order.already_cancelled</c> y no se anula otra vez; el pago
/// repetido llega con el estado que Quotations ya tiene y no cambia nada; el mismo <c>last_error</c> no sube la versión. Lo que
/// propaga el escritor (<c>RequestConcurrencyException</c>, errores transitorios) sale tal cual: la ingesta no guarda y la
/// entrega reintenta.</para>
/// </summary>
public sealed class ShopifyOrderChanges(IShopifyOrderWriter orders, IClock clock) : IShopifyOrderChangeReaction
{
    /// <summary>§6.3 (H1): el ancho del motivo de anulación del pedido.</summary>
    public const int CancellationReasonMaxLength = 500;

    private const string CancelledInShopify = "Cancelado en Shopify";
    private const string InvoicedBeforeCancelDetail = """{"reason":"invoiced_before_cancel"}""";

    public async Task ReactAsync(ShopifyOrderChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var (connection, store, row, previous, snapshot) = (change.Connection, change.Store, change.Row, change.Previous, change.Snapshot);
        if (store.TenantId != row.TenantId || store.ConnectionId != row.ConnectionId
            || connection.TenantId != row.TenantId || connection.ConnectionId != row.ConnectionId)
        {
            throw new ArgumentException(
                "The Shopify store, the mirror row and the connection must belong to the same tenant and connection.", nameof(change));
        }

        switch (row.ImportState)
        {
            case ShopifyImportState.Imported when snapshot.CancelledAt is not null:
                await CancelAsync(store, row, snapshot, cancellationToken);
                break;
            case ShopifyImportState.Imported when HasNewFinancialStatus(previous, snapshot):
                await SyncPaymentAsync(row, snapshot, cancellationToken);
                break;
            case ShopifyImportState.Waiting when snapshot.CancelledAt is not null:
                await CancelWaitingAsync(connection, store, row, snapshot, cancellationToken);
                break;
            default:
                // En espera sin cancelar: el pedido aún no existe y nacerá con el estado nuevo. Cancelled e Ignored: terminales.
                break;
        }
    }

    private async Task CancelWaitingAsync(
        ShopifyConnectionRef connection, ShopifyStore store, ShopifyOrderMirror row, ShopifyOrderSnapshot snapshot, CancellationToken cancellationToken)
    {
        var existing = await orders.FindByExternalKeyAsync(
            row.TenantId, ShopifyOrderImporter.ExternalKey(connection, snapshot.OrderId), cancellationToken);
        if (existing is null)
        {
            row.MarkIgnored();
            return;
        }

        // La fila quedó atrás del pedido: se pone al día con lo que Quotations tiene, como el AlreadyExisted del importador, y se
        // anula. Los avisos no se recalculan: el pedido se está anulando.
        row.MarkImported(existing.ClientId, existing.OrderId, snapshot.TotalPrice - existing.Total, null, clock.UtcNow);
        await CancelAsync(store, row, snapshot, cancellationToken);
    }

    private async Task CancelAsync(ShopifyStore store, ShopifyOrderMirror row, ShopifyOrderSnapshot snapshot, CancellationToken cancellationToken)
    {
        // Una fila importada viene de una tienda configurada, y Configure exige el asesor: sin él es un dato roto, no un aviso.
        var advisorId = store.AdvisorMembershipId
            ?? throw new InvalidOperationException("An imported Shopify order belongs to a store without an advisor.");
        var orderId = row.QepOrderId
            ?? throw new InvalidOperationException("An imported Shopify order has no QEP order.");
        var outcome = await orders.CancelAsync(row.TenantId, orderId, advisorId, Reason(snapshot.CancelReason), cancellationToken);
        switch (outcome.Kind)
        {
            case ShopifyOrderOutcomeKind.Applied:
            case ShopifyOrderOutcomeKind.AlreadyCancelled:
                // already_cancelled: no se hace nada en QEP, pero la fila sí cierra. Es lo que ve el reintento cuando la anulación
                // ya se hizo y el guardado del espejo se perdió; dejarla importada la dejaría así para siempre.
                row.MarkCancelled(null);
                break;
            case ShopifyOrderOutcomeKind.AlreadyInvoiced:
                row.MarkCancelled(InvoicedBeforeCancelDetail);
                break;
            default:
                // Tarea 12: cualquier otro rechazo queda ignorado y auditado; qué hacer con él lo decide el owner.
                row.RecordLastError(outcome.Code ?? outcome.Kind.ToString());
                break;
        }
    }

    private async Task SyncPaymentAsync(ShopifyOrderMirror row, ShopifyOrderSnapshot snapshot, CancellationToken cancellationToken)
    {
        var orderId = row.QepOrderId
            ?? throw new InvalidOperationException("An imported Shopify order has no QEP order.");
        var outcome = await orders.SyncPaymentStatusAsync(row.TenantId, orderId, snapshot.FinancialStatus, cancellationToken);
        if (outcome.Kind == ShopifyOrderOutcomeKind.Ignored && outcome.Code is { } code)
        {
            row.RecordLastError(code);
        }
    }

    /// <summary>§6.3: «otro <c>financial_status</c>». Uno vacío (Shopify no lo mandó) no es un cambio.</summary>
    private static bool HasNewFinancialStatus(ShopifyMirrorState previous, ShopifyOrderSnapshot snapshot) =>
        snapshot.FinancialStatus.Trim().Length > 0
        && !string.Equals(previous.FinancialStatus?.Trim(), snapshot.FinancialStatus.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Reason(string? cancelReason)
    {
        var reason = cancelReason?.Trim();
        var text = string.IsNullOrEmpty(reason) ? CancelledInShopify : $"{CancelledInShopify}: {reason}";
        return text.Length <= CancellationReasonMaxLength ? text : text[..CancellationReasonMaxLength];
    }
}
