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
/// <item><b>Importada con otro <c>financial_status</c></b> (también si la anulación del mismo aviso se rechazó): se sincroniza el pago; un rechazo (<c>not_pending</c>,
/// <c>payment_has_proofs</c>) va a <c>last_error</c>; <c>refunded</c>, <c>partially_refunded</c> y <c>voided</c> no dejan
/// rastro (ruling S19).</item>
/// <item><b>En espera y cancelada:</b> <c>Ignored</c>, salvo que el pedido ya exista en QEP con su clave externa (el worker cayó
/// entre crearlo y marcar la fila): entonces se marca importada, con los avisos que se pueden saber sin reimportar, y se anula
/// como cualquier otra.</item>
/// <item><b><c>last_error</c>:</b> uno solo por aviso, con los códigos rechazados unidos por <c>"; "</c>; si lo intentado salió
/// bien, se borra el que hubiera.</item>
/// <item><b><c>Cancelled</c> e <c>Ignored</c></b> son terminales (ruling S13): nada.</item>
/// </list>
/// <para><b>Idempotente.</b> Se repite en el bucle de concurrencia de la ingesta (hasta
/// <see cref="ShopifyOrderIngestion.MaxConcurrencyAttempts"/> veces por entrega) y cuando el guardado del espejo falla después de
/// actuar en Quotations: la anulación repetida responde <c>order.order.already_cancelled</c> y no se anula otra vez; el pago
/// repetido llega con el estado que Quotations ya tiene y no cambia nada; el mismo <c>last_error</c> no sube la versión. Lo que
/// propaga el escritor (<c>RequestConcurrencyException</c>, errores transitorios) sale tal cual: la ingesta no guarda y la
/// entrega reintenta.</para>
/// </summary>
public sealed class ShopifyOrderChanges(IShopifyOrderWriter orders, IShopifyCustomerDirectory customers, IClock clock)
    : IShopifyOrderChangeReaction
{
    /// <summary>§6.3 (H1): el ancho del motivo de anulación del pedido.</summary>
    public const int CancellationReasonMaxLength = 500;

    private const string CancelledInShopify = "Cancelado en Shopify";
    private const string InvoicedBeforeCancelDetail = """{"reason":"invoiced_before_cancel"}""";

    public async Task ReactAsync(ShopifyOrderChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(customers);
        var (connection, store, row, previous, snapshot) = (change.Connection, change.Store, change.Row, change.Previous, change.Snapshot);
        if (store.TenantId != row.TenantId || store.ConnectionId != row.ConnectionId
            || connection.TenantId != row.TenantId || connection.ConnectionId != row.ConnectionId)
        {
            throw new ArgumentException(
                "The Shopify store, the mirror row and the connection must belong to the same tenant and connection.", nameof(change));
        }

        var effect = new Effect();
        switch (row.ImportState)
        {
            case ShopifyImportState.Imported:
                await ImportedAsync(store, row, previous, snapshot, effect, cancellationToken);
                break;
            case ShopifyImportState.Waiting when snapshot.CancelledAt is not null:
                if (await CatchUpAsync(connection, store, row, snapshot, cancellationToken))
                {
                    await ImportedAsync(store, row, previous, snapshot, effect, cancellationToken);
                }

                break;
            default:
                // En espera sin cancelar: el pedido aún no existe y nacerá con el estado nuevo. Cancelled e Ignored: terminales.
                break;
        }

        // Un solo last_error para todo el aviso: si la anulación y el pago se rechazan, van los dos códigos; si lo que se intentó
        // salió bien, el error viejo se borra. Unchanged (reembolsos) no cuenta ni como éxito ni como error.
        if (effect.Errors.Count > 0)
        {
            row.RecordLastError(string.Join("; ", effect.Errors));
        }
        else if (effect.Succeeded)
        {
            row.ClearLastError();
        }
    }

    /// <summary>Anula si Shopify canceló; si la anulación no cerró la fila (rechazo <c>Ignored</c>) o no hubo cancelación, sincroniza
    /// el pago cuando cambió. Ronda 1, minor 2: <c>ApplySnapshot</c> ya movió el pago en la fila, así que un pago que no se
    /// sincroniza en este aviso no se vuelve a ver nunca.</summary>
    private async Task ImportedAsync(
        ShopifyStore store, ShopifyOrderMirror row, ShopifyMirrorState previous, ShopifyOrderSnapshot snapshot, Effect effect, CancellationToken cancellationToken)
    {
        if (snapshot.CancelledAt is not null)
        {
            await CancelAsync(store, row, snapshot, effect, cancellationToken);
        }

        if (row.ImportState == ShopifyImportState.Imported && HasNewFinancialStatus(previous, snapshot))
        {
            await SyncPaymentAsync(row, snapshot, effect, cancellationToken);
        }
    }

    /// <summary>Una fila en espera que se canceló: <c>Ignored</c> si el pedido no llegó a QEP; si llegó (el worker cayó entre crearlo y
    /// marcar la fila), la pone al día como el <c>AlreadyExisted</c> del importador y devuelve <c>true</c> para anularla. Los avisos
    /// que se pueden saber sin volver a importar se conservan: el cliente incompleto (con el documento que vino) y la diferencia de
    /// totales. Los del envío (dirección, transportadora, contraentrega) no: pedirían otra vez las búsquedas del alta para un pedido
    /// que se está anulando.</summary>
    private async Task<bool> CatchUpAsync(
        ShopifyConnectionRef connection, ShopifyStore store, ShopifyOrderMirror row, ShopifyOrderSnapshot snapshot, CancellationToken cancellationToken)
    {
        var existing = await orders.FindByExternalKeyAsync(
            row.TenantId, ShopifyOrderImporter.ExternalKey(connection, snapshot.OrderId), cancellationToken);
        if (existing is null)
        {
            row.MarkIgnored();
            return false;
        }

        var customer = await customers.FindByIdAsync(row.TenantId, existing.ClientId, cancellationToken);
        var difference = snapshot.TotalPrice - existing.Total;
        var warnings = ShopifyOrderImporter.WarningsJson(
            customer,
            ShopifyCustomerResolver.DocumentOf(store, snapshot),
            addressUnresolved: false,
            ShopifyOrderImporter.ExceedsTolerance(difference, snapshot.TotalPrice),
            shippingResult: null,
            carrierName: null);
        row.MarkImported(existing.ClientId, existing.OrderId, difference, warnings, clock.UtcNow);
        return true;
    }

    private async Task CancelAsync(
        ShopifyStore store, ShopifyOrderMirror row, ShopifyOrderSnapshot snapshot, Effect effect, CancellationToken cancellationToken)
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
                effect.Succeeded = true;
                break;
            case ShopifyOrderOutcomeKind.AlreadyInvoiced:
                row.MarkCancelled(InvoicedBeforeCancelDetail);
                effect.Succeeded = true;
                break;
            default:
                // Tarea 12: cualquier otro rechazo queda ignorado y auditado; qué hacer con él lo decide el owner.
                effect.Errors.Add(outcome.Code ?? outcome.Kind.ToString());
                break;
        }
    }

    private async Task SyncPaymentAsync(ShopifyOrderMirror row, ShopifyOrderSnapshot snapshot, Effect effect, CancellationToken cancellationToken)
    {
        var orderId = row.QepOrderId
            ?? throw new InvalidOperationException("An imported Shopify order has no QEP order.");
        var outcome = await orders.SyncPaymentStatusAsync(row.TenantId, orderId, snapshot.FinancialStatus, cancellationToken);
        switch (outcome.Kind)
        {
            case ShopifyOrderOutcomeKind.Ignored:
                effect.Errors.Add(outcome.Code ?? outcome.Kind.ToString());
                break;
            case ShopifyOrderOutcomeKind.Applied:
                effect.Succeeded = true;
                break;
            default:
                // Unchanged (refunded, partially_refunded, voided): no cambia nada, ni siquiera el error que ya había (S19).
                break;
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

    /// <summary>Lo que el aviso dejó: los códigos rechazados y si algo salió bien.</summary>
    private sealed class Effect
    {
        public List<string> Errors { get; } = [];

        public bool Succeeded { get; set; }
    }
}
