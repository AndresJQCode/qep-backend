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
/// Los cambios de pago y cancelación sobre una fila <c>Imported</c> (§6.3) son de la tarea 14: <c>ApplySnapshot</c> pisa
/// <c>FinancialStatus</c> y <c>CancelledAt</c>, así que quien los conecte acá tiene que leer el estado anterior antes de aplicar.
/// </summary>
public sealed class ShopifyOrderIngestion(
    IShopifyStoreRepository stores,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyOrderImporter importer,
    IShopifyUnitOfWork unitOfWork,
    IClock clock)
{
    public const int MaxConcurrencyAttempts = 3;

    private const string ConcurrencyConflict = "concurrency.conflict";

    // §5.4: el payload del espejo es el snapshot normalizado, en camelCase como los DTO de la API.
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public async Task<ShopifyOrderMirror> ApplyAsync(
        ShopifyConnectionRef connection, ShopifyOrderSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(snapshot);
        var header = snapshot.ToHeader();
        var payload = JsonSerializer.Serialize(snapshot, PayloadJson);
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
            store = ShopifyStore.Create(connection.ConnectionId, connection.TenantId, clock.UtcNow);
            stores.Add(store);
        }

        var row = await mirrors.FindAsync(connection.TenantId, connection.ConnectionId, snapshot.OrderId, cancellationToken);
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
            var existing = row;
            applied = Guarded(() => existing.ApplySnapshot(payload, snapshot.UpdatedAt, header));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (applied && connection.IsOperational && row.ImportState == ShopifyImportState.Waiting)
        {
            await importer.ImportAsync(store, row, snapshot, cancellationToken);
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
