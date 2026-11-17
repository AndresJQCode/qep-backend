using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Shopify.Domain;
using Modules.Tenancy.Application;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-11-17 §7: cuánto del catálogo de la tienda ya tiene par en QEP, para el paso del onboarding. BFF: la pantalla dibuja
/// «X de Y variantes» y la lista de las que faltan sin consultar nada más. <see cref="Matched"/> cuenta una variante con vínculo o
/// cuyo SKU recortado ya es el código de una variante de QEP; <see cref="CatalogEmpty"/> es «no hay ningún producto activo»
/// (sugerir importar todo); <see cref="MissingSample"/> son las primeras 20 sin par, en el orden de Shopify.
/// </summary>
public sealed record ShopifyProductCoverageDto(int ShopifyVariants, int Matched, bool CatalogEmpty, IReadOnlyList<ShopifyMissingVariantDto> MissingSample);

/// <summary>Una variante sin par: su SKU recortado (<c>null</c> si no tiene) y el título de su producto.</summary>
public sealed record ShopifyMissingVariantDto(string? Sku, string Title);

/// <summary><c>GET …/stores/{connectionId}/products/coverage?redirectUri=</c> (<c>integrations.connection.read</c>).</summary>
public sealed record GetShopifyProductCoverageQuery(Guid TenantId, Guid ConnectionId, string? RedirectUri) : IQuery<ShopifyProductCoverageDto>;

/// <summary>
/// El cálculo de la cobertura (spec 2026-11-17 §7), sin autorización ni guardado: las variantes de Shopify por
/// <see cref="IShopifyProductsSource.ListVariantKeysAsync"/> (todas las páginas), cuáles casan por vínculo o por SKU, si el
/// catálogo de QEP está vacío y hasta <see cref="MissingSampleSize"/> de muestra, con la hora del reloj. Lo usan el <c>GET</c> de
/// la cobertura y el BFF del asistente (spec onboarding §7 H7). Una falla de Shopify sale tal cual
/// (<see cref="ShopifyProductsSourceException"/>): cada llamador decide qué responde.
/// </summary>
public sealed class ShopifyProductCoverageCalculator(
    IShopifyProductsSource source,
    IShopifyProductLinkRepository links,
    IShopifyProductLookup products,
    IClock clock)
{
    public const int MissingSampleSize = 20;

    /// <summary>Los códigos se preguntan a Catalog en lotes, para no armar un <c>IN</c> de miles.</summary>
    public const int CodeBatchSize = 500;

    public async Task<ShopifyProductsCoverage> MeasureAsync(ShopifyConnectionRef connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var variants = new List<ShopifyVariantKey>();
        string? cursor = null;
        do
        {
            var page = await source.ListVariantKeysAsync(connection, cursor, cancellationToken);
            variants.AddRange(page.Variants);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        var linked = await links.ListLinkedShopifyVariantIdsAsync(connection.ConnectionId, cancellationToken);
        var candidates = variants
            .Where(variant => !linked.Contains(variant.VariantId))
            .Select(variant => Sku(variant))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in candidates.Chunk(CodeBatchSize))
        {
            existing.UnionWith(await products.FindExistingCodesAsync(connection.TenantId, batch, cancellationToken));
        }

        var missing = variants
            .Where(variant => !linked.Contains(variant.VariantId) && !(Sku(variant) is { } sku && existing.Contains(sku)))
            .ToArray();
        var catalogEmpty = await products.CountActiveAsync(connection.TenantId, cancellationToken) == 0;
        return new ShopifyProductsCoverage(
            variants.Count,
            variants.Count - missing.Length,
            catalogEmpty,
            [.. missing.Take(MissingSampleSize).Select(variant => new ShopifyCoverageSample(Sku(variant), variant.ProductTitle))],
            clock.UtcNow);
    }

    private static string? Sku(ShopifyVariantKey variant) => string.IsNullOrWhiteSpace(variant.Sku) ? null : variant.Sku.Trim();
}

/// <summary>
/// Consulta Shopify en el momento (todas las páginas de variantes). Las guardas son las del <c>POST</c> de la importación, sin la
/// empresa. Una falla de Shopify: credencial rechazada → 422 <c>shopify.store.not_operational</c>; scope retirado → 422
/// <c>scope_missing</c>; sin respuesta o <c>THROTTLED</c> → 503 <c>shopify.store.sync_unavailable</c>.
/// <para>Spec 2026-11-17 (onboarding) §7 H7: la medición se guarda en la tienda (la crea si el módulo todavía no la tiene), sin
/// subir su versión. Si el guardado pierde por concurrencia, la medición se descarta y la respuesta es la misma: guardarla es
/// para el asistente, no para quien pidió la cobertura.</para>
/// </summary>
public sealed class GetShopifyProductCoverageHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyConnectionDirectory connections,
    IShopifyReauthorization reauthorization,
    ShopifyProductCoverageCalculator calculator,
    IShopifyStoreRepository stores,
    IShopifyUnitOfWork unitOfWork,
    IClock clock) : IQueryHandler<GetShopifyProductCoverageQuery, ShopifyProductCoverageDto>
{
    public const int MissingSampleSize = ShopifyProductCoverageCalculator.MissingSampleSize;

    public const int CodeBatchSize = ShopifyProductCoverageCalculator.CodeBatchSize;

    public async Task<ShopifyProductCoverageDto> HandleAsync(GetShopifyProductCoverageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, ShopifyPermissions.ConnectionRead, cancellationToken);
        var connection = await ShopifyStoreSupport.ConnectionAsync(connections, query.TenantId, query.ConnectionId, cancellationToken);
        ShopifyProductGuards.EnsureOperational(connection);
        await ShopifyProductGuards.EnsureReadProductsAsync(reauthorization, connection, query.RedirectUri, cancellationToken);

        ShopifyProductsCoverage coverage;
        try
        {
            coverage = await calculator.MeasureAsync(connection, cancellationToken);
        }
        catch (ShopifyProductsSourceException exception)
        {
            throw exception.Failure switch
            {
                ShopifyProductsSourceFailure.Rejected => new ShopifyDomainException(
                    ShopifyErrorCodes.StoreNotOperational, "Shopify rejected the store credentials.", exception),
                ShopifyProductsSourceFailure.AccessDenied => new ShopifyProductsScopeMissingException(null),
                _ => new ServiceUnavailableException(
                    ShopifyErrorCodes.StoreSyncUnavailable, $"Shopify could not be read now ({exception.Failure}); try again later."),
            };
        }

        await RecordAsync(connection, coverage, cancellationToken);
        return ToDto(coverage);
    }

    /// <summary>La forma de siempre del <c>GET</c>: sin la hora de la medición, que es del asistente.</summary>
    public static ShopifyProductCoverageDto ToDto(ShopifyProductsCoverage coverage) =>
        new(
            coverage.ShopifyVariants,
            coverage.Matched,
            coverage.CatalogEmpty,
            [.. coverage.MissingSample.Select(sample => new ShopifyMissingVariantDto(sample.Sku, sample.Title))]);

    private async Task RecordAsync(ShopifyConnectionRef connection, ShopifyProductsCoverage coverage, CancellationToken cancellationToken)
    {
        try
        {
            var store = await ShopifyStoreSupport.FindOrCreateAsync(stores, unitOfWork, connection, clock.UtcNow, cancellationToken);
            store.RecordProductsCoverage(coverage);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (RequestConcurrencyException exception) when (ShopifyStoreSupport.IsConflict(exception))
        {
            // Un PUT de la tienda ganó entre la lectura y el guardado. La medición vuelve igual; el próximo GET la guarda.
            unitOfWork.DiscardPendingChanges();
        }
    }
}

/// <summary><c>POST …/stores/{connectionId}/products/coverage/accept</c> (<c>integrations.connection.manage</c>): «Seguir así».</summary>
public sealed record AcceptShopifyProductCoverageCommand(Guid TenantId, Guid ConnectionId) : ICommand<bool>;

/// <summary>
/// Spec 2026-11-17 (onboarding) §4.2: «Seguir así» marca el paso <c>products</c> como hecho con la cobertura que hay. En orden:
/// tenant, permiso y módulo (403); la conexión del tenant (403); la tienda (la crea si no existe); el instante (idempotente: se
/// conserva el primero) y la auditoría <c>shopify.products.coverage_accepted</c> en la misma transacción. No sube la versión de
/// la tienda: es estado del asistente, no configuración (Review Focus 2).
/// </summary>
public sealed class AcceptShopifyProductCoverageHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyConnectionDirectory connections,
    IShopifyStoreRepository stores,
    IShopifyUnitOfWork unitOfWork,
    IShopifyAuditRecorder auditRecorder,
    IClock clock) : ICommandHandler<AcceptShopifyProductCoverageCommand, bool>
{
    public async Task<bool> HandleAsync(AcceptShopifyProductCoverageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await ShopifyAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ShopifyPermissions.ConnectionManage, cancellationToken);
        var connection = await ShopifyStoreSupport.ConnectionAsync(connections, command.TenantId, command.ConnectionId, cancellationToken);

        var now = clock.UtcNow;
        var store = await ShopifyStoreSupport.FindOrCreateAsync(stores, unitOfWork, connection, now, cancellationToken);
        store.AcceptProductsCoverage(now);
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            AuditActorType.Human,
            ShopifyAuditActions.ProductsCoverageAccepted,
            ShopifyAuditActions.ConnectionResourceType,
            store.ConnectionId.ToString(),
            "[]",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
