using BuildingBlocks.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>Una importación reclamada: su id, su tenant y el número de intento que el reclamo acaba de anotar.</summary>
public sealed record ShopifyProductImportClaim(Guid ImportId, Guid TenantId, int Attempts);

/// <summary>
/// Spec 2026-11-17 §5.2: la cola de <c>shopify.product_imports</c>. El reclamo es un <c>UPDATE</c> que se confirma solo, con
/// <c>FOR UPDATE SKIP LOCKED</c> y lease: un solo ganador entre réplicas. Implementación en Infrastructure.
/// </summary>
// CA1711: «Queue» es el sustantivo del plan, como IShopifyOrderPushQueue; no hereda de System.Collections.Queue.
#pragma warning disable CA1711
public interface IShopifyProductImportQueue
#pragma warning restore CA1711
{
    /// <summary>La importación viva más vieja cuyo lease venció (o sin lease): la pasa a <c>Running</c>, le da
    /// <see cref="ShopifyProductImportProcessor.Lease"/> y suma un intento. <c>null</c> si no hay ninguna.</summary>
    Task<ShopifyProductImportClaim?> TryClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Mueve el lease (renovarlo mientras trabaja, o reprogramar un reintento). Sólo si sigue viva.</summary>
    Task RenewAsync(Guid importId, DateTimeOffset until, CancellationToken cancellationToken);

    /// <summary>La termina <c>Failed</c> sin cargarla (el worker, ante una excepción inesperada en el último intento).</summary>
    Task MarkFailedAsync(Guid importId, string code, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ShopifyProductSync"/> en un scope propio por llamada. Hace falta porque la sincronización descarta lo pendiente de su
/// <c>ShopifyDbContext</c> tras un rechazo o una carrera (<see cref="IShopifyUnitOfWork.DiscardPendingChanges"/>), y eso soltaría la
/// importación o la entrega que quien llama tiene cargada. Implementación en Infrastructure.
/// </summary>
public interface IShopifyProductSyncScope
{
    Task<ShopifyProductOutcome> ApplyAsync(ShopifyProductSyncContext context, ShopifyProductSnapshot product, CancellationToken cancellationToken);

    Task<ShopifyProductOutcome> DeleteAsync(ShopifyProductSyncContext context, long shopifyProductId, CancellationToken cancellationToken);
}

/// <summary>
/// Spec 2026-11-17 §5.2: corre una importación reclamada, página por página desde <see cref="ShopifyProductImport.Cursor"/>.
/// <list type="bullet">
/// <item>Conexión sin token → <c>token_expired</c>; sin <c>read_products</c> → <c>scope_missing</c>; tienda sin empresa →
/// <c>company_required</c>; moneda fuera del catálogo → <c>currency_unsupported</c>, antes de crear nada (P2).</item>
/// <item>Cada producto va por <see cref="IShopifyProductSyncScope"/> (su transacción, su candado, su scope); sus avisos quedan como
/// issues. En modo <c>Missing</c> se salta el producto cuyas variantes ya están todas vinculadas (cuenta como recorrido).</item>
/// <item>Cada página guarda contadores y cursor, y renueva el lease; la última, en el mismo guardado, termina <c>Done</c> y marca la
/// tienda con <c>products_synced_at</c>.</item>
/// <item>Fallas de Shopify: <c>Rejected</c> → <c>token_expired</c>; <c>AccessDenied</c> → <c>scope_missing</c>; <c>Throttled</c> o
/// <c>Unavailable</c> reintentan con el lease (<see cref="RetryDelay"/>) sin perder cursor ni contadores, hasta
/// <see cref="MaxAttempts"/>, y después <c>unreachable</c>.</item>
/// </list>
/// </summary>
public sealed class ShopifyProductImportProcessor(
    IShopifyProductImportRepository imports,
    IShopifyConnectionDirectory connections,
    ShopifyProductPricingResolver pricing,
    IShopifyProductsSource source,
    IShopifyProductLinkRepository links,
    IShopifyProductSyncScope sync,
    IShopifyProductImportQueue queue,
    IShopifyUnitOfWork unitOfWork,
    IClock clock)
{
    public const int ProductPageSize = 10;

    public const int MaxAttempts = 4;

    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan RetryStep = TimeSpan.FromSeconds(30);

    private static readonly ShopifyProductTally OneProduct = new(1, 0, 0, 0, 0);

    /// <summary>La espera antes del intento siguiente: el <c>Retry-After</c> de Shopify si lo mandó, si no 30 s por intento.</summary>
    public static TimeSpan RetryDelay(int attempts, TimeSpan? retryAfter) => retryAfter ?? RetryStep * Math.Max(1, attempts);

    public async Task ProcessAsync(ShopifyProductImportClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var import = await imports.FindAsync(claim.TenantId, claim.ImportId, cancellationToken);
        if (import is not { IsLive: true })
        {
            return;
        }

        var connection = await connections.FindAsync(import.TenantId, import.ConnectionId, cancellationToken);
        if (connection?.AccessToken is null)
        {
            await FailAsync(import, ShopifyProductCodes.TokenExpired, cancellationToken);
            return;
        }

        if (!ShopifyScopes.CanReadProducts(connection.Scopes))
        {
            await FailAsync(import, ShopifyProductCodes.ScopeMissing, cancellationToken);
            return;
        }

        try
        {
            await RunAsync(import, connection, cancellationToken);
        }
        catch (ShopifyProductsSourceException exception)
        {
            switch (exception.Failure)
            {
                case ShopifyProductsSourceFailure.Rejected:
                    await FailAsync(import, ShopifyProductCodes.TokenExpired, cancellationToken);
                    break;
                case ShopifyProductsSourceFailure.AccessDenied:
                    await FailAsync(import, ShopifyProductCodes.ScopeMissing, cancellationToken);
                    break;
                default:
                    if (claim.Attempts >= MaxAttempts)
                    {
                        await FailAsync(import, ShopifyProductCodes.Unreachable, cancellationToken);
                    }
                    else
                    {
                        // Vuelve a la cola: el cursor y los contadores son los de la última página guardada.
                        await queue.RenewAsync(import.Id, clock.UtcNow + RetryDelay(claim.Attempts, exception.RetryAfter), cancellationToken);
                    }

                    break;
            }
        }
    }

    private async Task RunAsync(ShopifyProductImport import, ShopifyConnectionRef connection, CancellationToken cancellationToken)
    {
        var resolution = await pricing.ResolveAsync(connection, cancellationToken);
        if (resolution.FailureCode is { } code)
        {
            await FailAsync(import, code, cancellationToken);
            return;
        }

        var store = resolution.Store!;
        if (import.Cursor is null && import.Done == 0 && import.Total == 0)
        {
            import.Start(resolution.Shop!.ProductsCount);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var linked = import.Mode == ShopifyProductImportMode.Missing
            ? await links.ListLinkedShopifyVariantIdsAsync(connection.ConnectionId, cancellationToken)
            : null;
        var context = new ShopifyProductSyncContext(connection, resolution.Pricing!, import.RequestedBySubjectId, ShopifySyncTrigger.Import);
        var cursor = import.Cursor;
        while (true)
        {
            var page = await source.FetchProductsAsync(connection, cursor, cancellationToken);
            var tally = ShopifyProductTally.Empty;
            foreach (var product in page.Products)
            {
                if (linked is not null && product.Variants.Count > 0 && product.Variants.All(variant => linked.Contains(variant.Id)))
                {
                    tally = tally.Add(OneProduct);
                    continue;
                }

                var outcome = await sync.ApplyAsync(context, product, cancellationToken);
                tally = tally.Add(outcome.Tally);
                foreach (var issue in outcome.IssueCodes)
                {
                    imports.AddIssue(ShopifyProductImportIssue.Create(import.Id, product.Id, product.Title, issue, clock.UtcNow));
                }

                // Un producto con imagen puede tardar: el lease se renueva por producto, no sólo por página.
                await queue.RenewAsync(import.Id, clock.UtcNow + Lease, cancellationToken);
            }

            import.RecordPage(tally, page.NextCursor);
            if (page.NextCursor is null)
            {
                // La última página, el fin y la marca de la tienda en un solo guardado: si choca, se reintenta desde la página
                // anterior sin contar dos veces.
                var now = clock.UtcNow;
                import.Complete(now);
                store.MarkProductsSynced(now);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await queue.RenewAsync(import.Id, clock.UtcNow + Lease, cancellationToken);
            cursor = page.NextCursor;
        }
    }

    private async Task FailAsync(ShopifyProductImport import, string code, CancellationToken cancellationToken)
    {
        import.Fail(code, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
