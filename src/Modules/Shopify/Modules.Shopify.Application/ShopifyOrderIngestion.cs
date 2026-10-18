using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §5.4 y §5.2 paso 4: el upsert del espejo y el intento de importación de un pedido ya normalizado. Lo usa el
/// webhook y está pensado para que la conciliación (tarea 15) pase por el mismo camino.
/// <list type="bullet">
/// <item>La tienda nace vacía en el primer contacto (§6.1), aunque la conexión no esté operativa.</item>
/// <item>El espejo se actualiza siempre; la importación, sólo si la conexión es operativa y la fila sigue <c>Waiting</c>. Un snapshot
/// más viejo que el guardado no pisa nada y tampoco dispara la importación (la conciliación reintenta los <c>Waiting</c>).</item>
/// <item>Una cabecera que el dominio no sabe guardar (<see cref="ArgumentException"/> de <c>ShopifyOrderMirror</c>) se vuelve
/// <see cref="ShopifyPayloadException"/>: es el mismo fallo permanente que un payload ilegible.</item>
/// <item>Perder la carrera con la conciliación o el reintento manual (<c>concurrency.conflict</c>: versión vieja de la fila, o
/// el mismo pedido o la misma tienda creados a la vez) descarta lo cargado, relee y vuelve a aplicar, hasta
/// <see cref="MaxConcurrencyAttempts"/> veces; si sigue chocando, lanza y la entrega vuelve con su lease.</item>
/// </list>
/// Los cambios de pago y cancelación sobre una fila que ya existía (§6.3) van a <see cref="IShopifyOrderChangeReaction"/> con el
/// estado anterior capturado antes de <c>ApplySnapshot</c>, y antes del guardado que persiste el snapshot (Ruling S14).
/// </summary>
public sealed class ShopifyOrderIngestion(
    IShopifyStoreRepository stores,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyOrderImporter importer,
    IShopifyOrderChangeReaction reaction,
    IShopifyUnitOfWork unitOfWork,
    IClock clock)
{
    public const int MaxConcurrencyAttempts = 3;

    private const string ConcurrencyConflict = "concurrency.conflict";

    // §5.4: el payload del espejo es el snapshot normalizado, en camelCase como los DTO de la API.
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <summary>El payload del espejo de un snapshot (§5.4).</summary>
    public static string WritePayload(ShopifyOrderSnapshot snapshot) => JsonSerializer.Serialize(snapshot, PayloadJson);

    /// <summary>El snapshot que guardó <see cref="WritePayload"/>: con él la conciliación reintenta una fila en espera (§6.4) por
    /// el mismo camino que un pedido recién traído.</summary>
    public static ShopifyOrderSnapshot ReadPayload(string payload) =>
        JsonSerializer.Deserialize<ShopifyOrderSnapshot>(payload, PayloadJson)
        ?? throw new ShopifyPayloadException("payload", "The mirror payload is empty.");

    public async Task<ShopifyOrderMirror> ApplyAsync(
        ShopifyConnectionRef connection, ShopifyOrderSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(snapshot);
        var header = snapshot.ToHeader();
        var payload = WritePayload(snapshot);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ApplyOnceAsync(connection, snapshot, header, payload, cancellationToken);
            }
            catch (RequestConcurrencyException exception)
                when (string.Equals(exception.Code, ConcurrencyConflict, StringComparison.Ordinal) && attempt < MaxConcurrencyAttempts)
            {
                // La instancia en memoria tiene la versión del perdedor: sin descartarla, releer devolvería la misma.
                unitOfWork.DiscardPendingChanges();
            }
        }
    }

    private async Task<ShopifyOrderMirror> ApplyOnceAsync(
        ShopifyConnectionRef connection,
        ShopifyOrderSnapshot snapshot,
        ShopifyOrderHeader header,
        string payload,
        CancellationToken cancellationToken)
    {
        var store = await stores.FindAsync(connection.TenantId, connection.ConnectionId, cancellationToken);
        if (store is null)
        {
            // D-S4 (ruling S25): la ventana de la conciliación arranca en el enlace, no en este primer webhook.
            store = ShopifyStoreSupport.NewStore(connection, clock.UtcNow);
            await ShopifyStoreSupport.AddNewAsync(stores, store, cancellationToken);
        }

        // Ruling S16: el dominio queda en la tienda para que shop/redact la encuentre aunque la conexión se borre.
        if (!string.IsNullOrWhiteSpace(connection.ShopDomain)
            && !string.Equals(store.ShopDomain, connection.ShopDomain.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            store.RecordShopDomain(connection.ShopDomain);
        }

        var row = await mirrors.FindAsync(connection.TenantId, connection.ConnectionId, snapshot.OrderId, cancellationToken);
        ShopifyMirrorState? previous = null;
        bool applied;
        if (row is null)
        {
            row = Guarded(() => ShopifyOrderMirror.Create(
                connection.TenantId, connection.ConnectionId, snapshot.OrderId, payload, snapshot.UpdatedAt, header));
            mirrors.Add(row);
            applied = true;
        }
        else
        {
            // Ruling S14: ApplySnapshot pisa FinancialStatus y CancelledAt; lo que §6.3 compara se lee antes.
            previous = ShopifyMirrorState.Of(row);
            var existing = row;
            applied = Guarded(() => existing.ApplySnapshot(payload, snapshot.UpdatedAt, header));
        }

        // Ruling S14: la reacción va antes del guardado, en la misma unidad de trabajo. Si falla no se guarda el snapshot, la
        // entrega vuelve con su lease y el reintento ve el mismo estado anterior: el cambio no se pierde.
        if (applied && previous is not null)
        {
            await reaction.ReactAsync(new ShopifyOrderChange(connection, store, row, previous, snapshot), cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (applied && connection.IsOperational && row.ImportState == ShopifyImportState.Waiting)
        {
            await importer.ImportAsync(connection, store, row, snapshot, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return row;
    }

    private static T Guarded<T>(Func<T> write)
    {
        try
        {
            return write();
        }
        catch (ArgumentException exception)
        {
            throw new ShopifyPayloadException(exception.Message, exception);
        }
    }
}
