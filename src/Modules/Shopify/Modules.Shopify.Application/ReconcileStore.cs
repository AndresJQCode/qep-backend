using System.Text.Json.Serialization;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §5.3 y §6.4: la conciliación de una tienda, la del cron (<c>ShopifyReconcileWorker</c>) y la de «Sincronizar
/// ahora» (<see cref="SyncShopifyStoreHandler"/>).
/// <list type="number">
/// <item>Toma el lease de la tienda (<see cref="IShopifyReconcileLeases"/>); si otra corrida lo tiene, 409
/// <c>shopify.store.sync_in_progress</c> sin tocar nada.</item>
/// <item>Trae, página por página, los pedidos con <c>updated_at ≥ last_reconciled_at − </c><see cref="Overlap"/> y pasa cada uno por
/// <see cref="ShopifyOrderIngestion.ApplyAsync"/>, el mismo camino del webhook: espejo, reacción de §6.3 con el estado anterior y
/// el bucle de <c>concurrency.conflict</c> contra el worker de entregas.</item>
/// <item>Reintenta los <c>Waiting</c> de la tienda, los más viejos primero, hasta <see cref="WaitingRetryLimit"/>, por el mismo camino
/// (con el snapshot que guardó el espejo), salvo los que la corrida acaba de traer.</item>
/// <item>Sólo si nada falló mueve <c>last_reconciled_at</c> al instante en que <b>empezó</b> la corrida.</item>
/// </list>
/// <para><b>Por qué la ventana cubre lo que el webhook perdió.</b> La conciliación no depende de los webhooks: todo pedido cuyo
/// <c>updated_at</c> sea posterior al comienzo de la última corrida completa (menos el solape) entra en la siguiente. Un webhook
/// abandonado a los 8 intentos o descartado con 200 no cambia eso. Que la ventana arranque en el comienzo y no en el final de la
/// corrida anterior cubre el pedido que cambió mientras ella corría, después de traer su página; los 5 minutos de solape cubren el
/// desfase entre el reloj de QEP y el de Shopify y el retraso con que el índice de búsqueda de Shopify ve un cambio. Una corrida
/// que falla (Shopify caído, <c>THROTTLED</c> tres veces, un pedido que no se pudo aplicar) no mueve la ventana: la siguiente
/// vuelve a traer todo desde el mismo punto, y el upsert es idempotente.</para>
/// <para>Un pedido que falla no frena a los demás: lo que dejó pendiente en la unidad de trabajo se descarta para que el guardado
/// del siguiente no lo confirme (Ruling S14: un snapshot no se guarda sin su reacción).</para>
/// </summary>
public sealed partial class ReconcileStore(
    IShopifyOrdersSource source,
    ShopifyOrderIngestion ingestion,
    IShopifyStoreRepository stores,
    IShopifyOrderMirrorRepository mirrors,
    IShopifyReconcileLeases leases,
    IShopifyConnectionHealth health,
    IShopifyUnitOfWork unitOfWork,
    IClock clock,
    TimeProvider time,
    ILogger<ReconcileStore> logger)
{
    /// <summary>D-S1: 5 minutos de solape con la corrida anterior.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    /// <summary>Lo que dura el lease si el pod muere a mitad de corrida; una corrida normal lo suelta al terminar.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);

    /// <summary>§5.3: <c>THROTTLED</c> o 429 sin <c>Retry-After</c>.</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(2);

    /// <summary>D-S1: tope de reintentos de <c>Waiting</c> por corrida.</summary>
    public const int WaitingRetryLimit = 200;

    /// <summary>§5.3: tras tres <c>THROTTLED</c> seguidos, la tienda se deja para la próxima corrida.</summary>
    public const int MaxConsecutiveThrottles = 3;

    private const string ConcurrencyConflict = "concurrency.conflict";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shopify order {OrderId} of store {ConnectionId} (tenant {TenantId}) skipped by the reconciliation: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string orderId, Guid connectionId, Guid tenantId, string reason);

    public async Task<ShopifySyncResult> RunAsync(ShopifyConnectionRef connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!connection.IsOperational || string.IsNullOrWhiteSpace(connection.AccessToken))
        {
            throw NotOperational();
        }

        var started = time.GetTimestamp();
        var startedAt = clock.UtcNow;
        var store = await EnsureStoreAsync(connection, startedAt, cancellationToken);
        if (await leases.TryAcquireAsync(connection.TenantId, connection.ConnectionId, LeaseDuration, cancellationToken) is not { } lease)
        {
            throw new ResourceConflictException(
                ShopifyErrorCodes.StoreSyncInProgress, "A reconciliation of this Shopify store is already running.");
        }

        try
        {
            var run = new Run();
            var since = (store.LastReconciledAt ?? startedAt) - Overlap;
            string? cursor = null;
            do
            {
                var page = await FetchAsync(connection, since, cursor, cancellationToken);
                foreach (var snapshot in page.Orders)
                {
                    run.Fetched++;
                    await ApplyAsync(connection, snapshot, run, cancellationToken);
                }

                foreach (var unreadable in page.Skipped)
                {
                    await RecordPermanentFailureAsync(connection, unreadable.OrderId, unreadable.Reason, run, cancellationToken);
                }

                cursor = page.NextCursor;
            }
            while (cursor is not null);

            foreach (var waiting in await mirrors.ListWaitingAsync(
                connection.TenantId, connection.ConnectionId, WaitingRetryLimit, cancellationToken))
            {
                if (run.Touched.Contains(waiting.ShopifyOrderId))
                {
                    continue;
                }

                ShopifyOrderSnapshot snapshot;
                try
                {
                    snapshot = ShopifyOrderIngestion.ReadPayload(waiting.Payload);
                }
                catch (Exception exception) when (exception is System.Text.Json.JsonException or ShopifyPayloadException)
                {
                    await RecordPermanentFailureAsync(connection, waiting.ShopifyOrderId, Describe(exception), run, cancellationToken);
                    continue;
                }

                await ApplyAsync(connection, snapshot, run, cancellationToken);
            }

            if (run.Failed == 0)
            {
                await leases.RecordReconciledAsync(connection.TenantId, connection.ConnectionId, startedAt, cancellationToken);
            }

            return run.Result(time.GetElapsedTime(started));
        }
        finally
        {
            // Sin el token del llamador: una corrida cancelada también suelta su lease. Sólo el suyo (minor 5).
            await leases.ReleaseAsync(connection.TenantId, connection.ConnectionId, lease, CancellationToken.None);
        }
    }

    // La fila de la tienda tiene que existir para que el UPDATE del lease la encuentre. Una que la conciliación ve por primera vez
    // nace con la ventana en este instante: no se trae historial (D-S4).
    private async Task<ShopifyStore> EnsureStoreAsync(ShopifyConnectionRef connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (await stores.FindAsync(connection.TenantId, connection.ConnectionId, cancellationToken) is { } existing)
            {
                return existing;
            }

            // D-S4 (ruling S25): la ventana arranca en el enlace, no en el primer contacto.
            var store = ShopifyStore.Create(connection.ConnectionId, connection.TenantId, connection.LinkedAt ?? now);
            if (!string.IsNullOrWhiteSpace(connection.ShopDomain))
            {
                store.RecordShopDomain(connection.ShopDomain);
            }

            stores.Add(store);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return store;
            }
            catch (RequestConcurrencyException exception)
                when (string.Equals(exception.Code, ConcurrencyConflict, StringComparison.Ordinal) && attempt < 2)
            {
                // El webhook la creó a la vez: se lee la suya.
                unitOfWork.DiscardPendingChanges();
            }
        }
    }

    private async Task<ShopifyOrdersPage> FetchAsync(
        ShopifyConnectionRef connection, DateTimeOffset since, string? cursor, CancellationToken cancellationToken)
    {
        for (var throttles = 0; ;)
        {
            try
            {
                return await source.FetchUpdatedSinceAsync(connection, since, cursor, cancellationToken);
            }
            catch (ShopifyOrdersSourceException exception) when (exception.Failure == ShopifyOrdersSourceFailure.Throttled)
            {
                if (++throttles >= MaxConsecutiveThrottles)
                {
                    throw Unavailable(exception);
                }

                await Task.Delay(exception.RetryAfter ?? DefaultRetryAfter, time, cancellationToken);
            }
            catch (ShopifyOrdersSourceException exception) when (exception.Failure == ShopifyOrdersSourceFailure.Rejected)
            {
                // §5.3: la conexión queda NeedsAttention y sale del ciclo hasta que vuelva a Active.
                await health.ReportRejectedAsync(connection.TenantId, connection.ConnectionId, cancellationToken);
                throw NotOperational(exception);
            }
            catch (ShopifyOrdersSourceException exception)
            {
                throw Unavailable(exception);
            }
        }
    }

    private async Task ApplyAsync(ShopifyConnectionRef connection, ShopifyOrderSnapshot snapshot, Run run, CancellationToken cancellationToken)
    {
        try
        {
            var row = await ingestion.ApplyAsync(connection, snapshot, cancellationToken);
            run.Touched.Add(row.ShopifyOrderId);
            run.States[row.ShopifyOrderId] = row.ImportState;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ShopifyPayloadException exception)
        {
            // Ruling S24, la regla del webhook: un fallo permanente (una cabecera que el dominio no guarda) se cierra con last_error
            // y no frena la ventana. Reintentarlo da lo mismo, y congelarla clavaría la tienda en este pedido para siempre.
            unitOfWork.DiscardPendingChanges();
            run.States.Remove(snapshot.OrderId);
            await RecordPermanentFailureAsync(connection, snapshot.OrderId, Describe(exception), run, cancellationToken);
        }
        catch (Exception exception)
        {
            // Transitorio (base, Quotations, concurrencia agotada): la ventana no avanza y la próxima corrida lo vuelve a traer.
            unitOfWork.DiscardPendingChanges();
            run.Touched.Add(snapshot.OrderId);
            run.States.Remove(snapshot.OrderId);
            run.Fail(exception);
        }
    }

    /// <summary>
    /// Un pedido que no se puede aplicar ni se podrá: se cuenta (<see cref="ShopifySyncResult.Skipped"/>) y, si su fila del espejo
    /// existe, queda con <paramref name="reason"/> en <c>last_error</c> para que la bandeja lo muestre. Un pedido nuevo con la
    /// cabecera rota no tiene fila (el dominio no la guarda): queda contado y en el log del worker. Perder la carrera al anotar no
    /// importa (otro escritor tocó la fila); cualquier otra falla al guardar sí es transitoria y frena la ventana.
    /// </summary>
    private async Task RecordPermanentFailureAsync(
        ShopifyConnectionRef connection, long? orderId, string reason, Run run, CancellationToken cancellationToken)
    {
        run.Skipped++;
        // Ronda 2: sin fila del espejo (pedido nuevo con la cabecera rota, o un GID no numérico) éste es el único rastro, porque la
        // ventana avanza. Sólo tenant, conexión, id y motivo: el motivo es el texto fijo de la excepción del parser o del dominio,
        // que nombra el campo y nunca su valor; nada del comprador ni del cuerpo.
        LogSkipped(logger, orderId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown", connection.ConnectionId, connection.TenantId, reason);
        if (orderId is not { } id)
        {
            return;
        }

        run.Touched.Add(id);
        try
        {
            if (await mirrors.FindAsync(connection.TenantId, connection.ConnectionId, id, cancellationToken) is { } row)
            {
                row.RecordLastError(reason);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }
        catch (RequestConcurrencyException exception)
            when (string.Equals(exception.Code, ConcurrencyConflict, StringComparison.Ordinal))
        {
            unitOfWork.DiscardPendingChanges();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            unitOfWork.DiscardPendingChanges();
            run.Fail(exception);
        }
    }

    /// <summary>El <c>last_error</c> con el formato del webhook (<c>invalid_payload: campo: mensaje</c>).</summary>
    private static string Describe(Exception exception) => exception switch
    {
        ShopifyPayloadException { Field.Length: > 0 } payload =>
            $"{ShopifyDeliveryResult.InvalidPayloadPrefix}: {payload.Field}: {payload.Message}",
        ShopifyPayloadException payload => $"{ShopifyDeliveryResult.InvalidPayloadPrefix}: {payload.Message}",
        _ => $"{ShopifyDeliveryResult.InvalidPayloadPrefix}: payload: The mirror payload is not a readable snapshot.",
    };

    private static ShopifyDomainException NotOperational(Exception? inner = null) => inner is null
        ? new(ShopifyErrorCodes.StoreNotOperational, "The Shopify connection is not operational.")
        : new(ShopifyErrorCodes.StoreNotOperational, "Shopify rejected the store credentials; the connection was reported.", inner);

    private static ServiceUnavailableException Unavailable(ShopifyOrdersSourceException exception) =>
        new(ShopifyErrorCodes.StoreSyncUnavailable, $"Shopify could not be reconciled now ({exception.Failure}); the next run retries.");

    private sealed class Run
    {
        public int Fetched { get; set; }

        public int Failed { get; private set; }

        public int Skipped { get; set; }

        /// <summary>Los pedidos que la corrida ya intentó: el reintento de los <c>Waiting</c> no los repite.</summary>
        public HashSet<long> Touched { get; } = [];

        public string? FirstError { get; private set; }

        /// <summary>El estado final de cada pedido que la corrida tocó, por id de Shopify.</summary>
        public Dictionary<long, ShopifyImportState> States { get; } = [];

        public void Fail(Exception exception)
        {
            Failed++;
            FirstError ??= exception.GetType().Name;
        }

        public ShopifySyncResult Result(TimeSpan elapsed) => new(
            Fetched,
            Count(ShopifyImportState.Imported),
            Count(ShopifyImportState.Waiting),
            Count(ShopifyImportState.Cancelled),
            Count(ShopifyImportState.Ignored),
            (long)elapsed.TotalMilliseconds)
        {
            Failed = Failed,
            FirstError = FirstError,
            Skipped = Skipped,
        };

        private int Count(ShopifyImportState state) => States.Values.Count(value => value == state);
    }
}

/// <summary>
/// §5.3 y §7: lo que responde «Sincronizar ahora» y lo que el cron deja en su log. <see cref="Fetched"/> son los pedidos que
/// Shopify devolvió en la ventana; <see cref="Imported"/>, <see cref="Waiting"/>, <see cref="Cancelled"/> e <see cref="Ignored"/>
/// cuentan cómo <b>quedaron</b> todos los pedidos que la corrida tocó (los traídos y los <c>Waiting</c> reintentados), no sólo los
/// que cambiaron: es lo que el aviso de la pantalla dice («3 importados, 2 en espera») sin que el cliente tenga que pedir la
/// bandeja para entenderlo. Los cuatro viajan siempre, también en cero (BFF). <see cref="Failed"/> (transitorios: frenan la
/// ventana), <see cref="FirstError"/> (sólo el tipo de la excepción, nunca su mensaje) y <see cref="Skipped"/> (permanentes: quedan
/// con <c>last_error</c> en el espejo y no frenan la ventana) son para el log del worker y no viajan: §7 fija la forma de la respuesta.
/// </summary>
public sealed record ShopifySyncResult(int Fetched, int Imported, int Waiting, int Cancelled, int Ignored, long DurationMs)
{
    [JsonIgnore]
    public int Failed { get; init; }

    [JsonIgnore]
    public string? FirstError { get; init; }

    [JsonIgnore]
    public int Skipped { get; init; }
}
