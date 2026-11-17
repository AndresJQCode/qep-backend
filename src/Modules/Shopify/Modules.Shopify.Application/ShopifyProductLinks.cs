using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Shopify.Domain;
using Modules.Tenancy.Application;

namespace Modules.Shopify.Application;

/// <summary><c>DELETE /api/v1/tenants/{tenantId}/shopify/products/{productId}/link</c> (<c>integrations.connection.manage</c>).</summary>
public sealed record UnlinkShopifyProductCommand(Guid TenantId, Guid ProductId) : ICommand<bool>;

/// <summary>
/// Spec 2026-11-17 §5.5: desvincular un producto. Busca los vínculos del producto en <b>cualquier</b> tienda del tenant (el detalle
/// de Catalog no conoce la conexión, ajuste H13); sin ninguno → 404 <c>shopify.products.link_not_found</c>. En una transacción: toma
/// el candado de cada producto de Shopify vinculado (el mismo que el webhook y la importación, para que ninguno vuelva a vincular a
/// la vez), relee, borra los vínculos y libera el producto en Catalog (<c>external_source</c> en <c>null</c>), con la auditoría.
/// Desde ahí el producto es propio de QEP: los webhooks lo tratan como uno sin vínculos.
/// </summary>
public sealed class UnlinkShopifyProductHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyProductLinkRepository links,
    IShopifyCatalogWriter catalog,
    IShopifyUnitOfWork unitOfWork,
    IShopifyAuditRecorder auditRecorder,
    IClock clock) : ICommandHandler<UnlinkShopifyProductCommand, bool>
{
    public async Task<bool> HandleAsync(UnlinkShopifyProductCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await ShopifyAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ShopifyPermissions.ConnectionManage, cancellationToken);

        var found = await links.FindByProductInTenantAsync(command.TenantId, command.ProductId, cancellationToken);
        if (found.Count == 0)
        {
            throw NotFound();
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // Orden fijo de los candados: dos desvínculos del mismo producto no se esperan en cruz.
        foreach (var (connectionId, shopifyProductId) in found
            .Select(link => (link.ConnectionId, link.ShopifyProductId))
            .Distinct()
            .OrderBy(key => key.ConnectionId)
            .ThenBy(key => key.ShopifyProductId))
        {
            await unitOfWork.LockProductAsync(connectionId, shopifyProductId, cancellationToken);
        }

        // Relee bajo el candado: lo de antes pudo cambiar mientras se esperaba.
        unitOfWork.DiscardPendingChanges();
        var current = await links.FindByProductInTenantAsync(command.TenantId, command.ProductId, cancellationToken);
        if (current.Count == 0)
        {
            throw NotFound();
        }

        foreach (var link in current)
        {
            links.Remove(link);
        }

        var now = clock.UtcNow;
        await catalog.ReleaseAsync(command.TenantId, command.ProductId, executionContext.SubjectId, cancellationToken);
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            AuditActorType.Human,
            ShopifyAuditActions.ProductUnlinked,
            ShopifyAuditActions.ProductResourceType,
            command.ProductId.ToString(),
            JsonSerializer.Serialize(new
            {
                connections = current.Select(link => link.ConnectionId.ToString()).Distinct().ToArray(),
                links = current.Count,
            }),
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static ResourceNotFoundException NotFound() =>
        new(ShopifyErrorCodes.ProductsLinkNotFound, "The product has no Shopify link in this tenant.");
}
