using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §5.2: la conexión de Integrations que corresponde a una tienda, por su dominio (<c>connection_routes</c>). Puerto
/// del módulo con adaptador en Bootstrapper (tarea 12): Shopify no referencia Integrations (ShopifyLayerTests).
/// </summary>
public interface IShopifyConnectionDirectory
{
    /// <summary><c>null</c> si ninguna ruta apunta a esa tienda: el webhook se cierra como <c>unknown_shop</c>.</summary>
    Task<ShopifyConnectionRef?> FindByShopDomainAsync(string shopDomain, CancellationToken cancellationToken);
}

/// <summary>
/// Lo que el módulo sabe de una conexión. <see cref="IsOperational"/> es conexión <c>Active</c> <b>y</b> módulo <c>shopify</c>
/// activo en el tenant: sin eso el espejo se actualiza igual y no se importa (§5.2). <see cref="AccessToken"/> sólo viaja si es
/// operativa y nunca se imprime: <see cref="ToString"/> lo omite, para que un log del registro no lo filtre (§12).
/// </summary>
public sealed record ShopifyConnectionRef(Guid TenantId, Guid ConnectionId, string ShopId, bool IsOperational, string? AccessToken)
{
    public override string ToString() =>
        $"ShopifyConnectionRef {{ TenantId = {TenantId}, ConnectionId = {ConnectionId}, ShopId = {ShopId}, IsOperational = {IsOperational} }}";
}

/// <summary>Spec 2026-10-15 §5.2 y §5.3: avisarle a Integrations que la tienda dejó de responder. Adaptador en Bootstrapper sobre
/// <c>IConnectionHealthReporter</c> (tarea 12): <see cref="ReportUninstalledAsync"/> con <c>app_uninstalled</c>,
/// <see cref="ReportRejectedAsync"/> con <c>credentials_rejected</c>.</summary>
public interface IShopifyConnectionHealth
{
    Task ReportUninstalledAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    Task ReportRejectedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>
/// Spec 2026-10-15 §6.2: el intento de importar una fila en espera. Lo implementa <c>ShopifyOrderImporter</c> (tarea 13); acá está
/// el contrato para que el webhook y la conciliación lo llamen igual. Sólo evalúa y escribe la fila; el commit es del llamador.
/// </summary>
public interface IShopifyOrderImporter
{
    Task<ShopifyImportOutcome> ImportAsync(
        ShopifyStore store, ShopifyOrderMirror row, ShopifyOrderSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>Cómo quedó la fila después del intento. La tarea 13 puede ampliarlo; la conciliación (tarea 15) cuenta por
/// <see cref="State"/>.</summary>
public sealed record ShopifyImportOutcome(ShopifyImportState State);
