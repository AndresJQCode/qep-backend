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
public sealed record ShopifyConnectionRef(
    Guid TenantId, Guid ConnectionId, string ShopId, string ShopDomain, bool IsOperational, string? AccessToken)
{
    public override string ToString() =>
        $"ShopifyConnectionRef {{ TenantId = {TenantId}, ConnectionId = {ConnectionId}, ShopId = {ShopId}, ShopDomain = {ShopDomain}, IsOperational = {IsOperational} }}";
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

/// <summary>Lo que §6.3 compara de una fila: el estado de importación, el pago y la cancelación.</summary>
public sealed record ShopifyMirrorState(ShopifyImportState ImportState, string? FinancialStatus, DateTimeOffset? CancelledAt)
{
    public static ShopifyMirrorState Of(ShopifyOrderMirror row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(row.ImportState, row.FinancialStatus, row.CancelledAt);
    }
}

/// <summary>Un snapshot que se aplicó sobre una fila que ya existía. <see cref="Previous"/> es la fila antes de
/// <c>ApplySnapshot</c>, que pisa <c>FinancialStatus</c> y <c>CancelledAt</c>; <see cref="Row"/> ya tiene el snapshot nuevo.</summary>
public sealed record ShopifyOrderChange(
    ShopifyConnectionRef Connection, ShopifyStore Store, ShopifyOrderMirror Row, ShopifyMirrorState Previous, ShopifyOrderSnapshot Snapshot);

/// <summary>
/// Ruling S14 y spec §6.3: la reacción a un cambio de pago o cancelación sobre una fila que ya existía. La implementa la tarea 14
/// (<c>ShopifyOrderChanges</c>); hasta entonces el registro por defecto es <see cref="NoShopifyOrderChangeReaction"/>.
/// <para>Corre <b>antes</b> del guardado que persiste el snapshot, en la misma unidad de trabajo: lo que escriba en la fila (p. ej.
/// <c>last_error</c>, <c>MarkCancelled</c>) entra en el mismo commit que el snapshot, y si lanza no se guarda nada, la entrega
/// vuelve con su lease y el reintento ve el mismo estado anterior. Por eso tiene que ser idempotente con lo que ya hizo afuera
/// (el pedido de QEP ya cancelado o ya con ese estado de pago): un fallo después de su efecto externo la vuelve a llamar.</para>
/// </summary>
public interface IShopifyOrderChangeReaction
{
    Task ReactAsync(ShopifyOrderChange change, CancellationToken cancellationToken);
}

/// <summary>La reacción vacía mientras la tarea 14 no registre la suya.</summary>
public sealed class NoShopifyOrderChangeReaction : IShopifyOrderChangeReaction
{
    public Task ReactAsync(ShopifyOrderChange change, CancellationToken cancellationToken) => Task.CompletedTask;
}
