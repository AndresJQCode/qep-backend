using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §5.4 y §5.2 paso 4: el upsert del espejo y el intento de importación de un pedido ya normalizado. Es el único
/// camino: lo usan el webhook, la conciliación y el reintento manual.
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
/// estado anterior capturado antes de <c>ApplySnapshot</c>, y antes del guardado que persiste el snapshot.
/// </summary>
public sealed class ShopifyOrderIngestion(
    IShopifyStoreRepository stores,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyOrderImporter importer,
    IShopifyOrderChangeReaction reaction,
    IShopifyOrderEditDetector editDetector,
    IShopifyUnitOfWork unitOfWork,
    IClock clock)
{
    public const int MaxConcurrencyAttempts = 3;

    private const string ConcurrencyConflict = "concurrency.conflict";

    // Los nombres en el payload (camelCase) de la dirección de envío y su zip.
    private const string ShippingAddressKey = "shippingAddress";

    private const string ZipKey = "zip";

    // §5.4: el payload del espejo es el snapshot normalizado, en camelCase como los DTO de la API.
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <summary>El payload del espejo de un snapshot (§5.4). Un zip desconocido (<see cref="ShopifyAddress.ZipUnrecorded"/>) se
    /// escribe como estaba, sin la clave: reescribir un payload viejo no lo convierte en uno que dice que no tenía zip.</summary>
    public static string WritePayload(ShopifyOrderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ShippingAddress is not { ZipUnrecorded: true })
        {
            return JsonSerializer.Serialize(snapshot, PayloadJson);
        }

        var node = JsonSerializer.SerializeToNode(snapshot, PayloadJson)!;
        node[ShippingAddressKey]!.AsObject().Remove(ZipKey);
        return node.ToJsonString(PayloadJson);
    }

    /// <summary>El snapshot que guardó <see cref="WritePayload"/>: con él la conciliación reintenta una fila en espera (§6.4) por
    /// el mismo camino que un pedido recién traído. Una dirección de envío sin la clave <c>zip</c> sale con
    /// <see cref="ShopifyAddress.ZipUnrecorded"/>.</summary>
    public static ShopifyOrderSnapshot ReadPayload(string payload)
    {
        var snapshot = JsonSerializer.Deserialize<ShopifyOrderSnapshot>(payload, PayloadJson)
            ?? throw new ShopifyPayloadException("payload", "The mirror payload is empty.");
        return snapshot.ShippingAddress is { Zip: null } address && LacksShippingZipKey(payload)
            ? snapshot with { ShippingAddress = address with { ZipUnrecorded = true } }
            : snapshot;
    }

    private static bool LacksShippingZipKey(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty(ShippingAddressKey, out var address)
            && address.ValueKind == JsonValueKind.Object
            && !address.TryGetProperty(ZipKey, out _);
    }

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
            // D-S4: la ventana de la conciliación arranca en el enlace, no en este primer webhook.
            store = ShopifyStoreSupport.NewStore(connection, clock.UtcNow);
            await ShopifyStoreSupport.AddNewAsync(stores, store, cancellationToken);
        }

        // El dominio queda en la tienda para que shop/redact la encuentre aunque la conexión se borre.
        if (!string.IsNullOrWhiteSpace(connection.ShopDomain)
            && !string.Equals(store.ShopDomain, connection.ShopDomain.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            store.RecordShopDomain(connection.ShopDomain);
        }

        var row = await mirrors.FindAsync(connection.TenantId, connection.ConnectionId, snapshot.OrderId, cancellationToken);
        ShopifyMirrorState? previous = null;
        string? previousPayload = null;
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
            // ApplySnapshot pisa FinancialStatus y CancelledAt; lo que §6.3 compara se lee antes.
            previous = ShopifyMirrorState.Of(row);
            // Spec 2026-11-14 (plan D6): la base de una fila importada antes de la sincronización es el payload que tenía.
            previousPayload = row.Payload;
            var existing = row;
            applied = Guarded(() => existing.ApplySnapshot(payload, snapshot.UpdatedAt, header));
        }

        // La detección de ediciones (spec 2026-11-14 §3) y la reacción de §6.3 van antes del guardado, en la misma unidad de
        // trabajo y con el mismo cambio. Si fallan no se guarda el snapshot, la entrega vuelve con su lease y el reintento ve el
        // mismo estado anterior: el cambio no se pierde. El detector no escribe en el pedido de QEP: eso va después del guardado.
        if (applied && previous is not null)
        {
            var change = new ShopifyOrderChange(connection, store, row, previous, snapshot);
            await editDetector.DetectAsync(change, previousPayload!, cancellationToken);
            await reaction.ReactAsync(change, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Con el cambio ya guardado, la tienda automática lo aplica. Corre también cuando el snapshot no es nuevo: si el guardado de
        // un intento anterior cayó después de que Quotations confirmó el pedido, el reintento termina ese cambio (review I-1).
        if (previous is not null && await editDetector.ApplyPendingAsync(store, row, cancellationToken))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

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
