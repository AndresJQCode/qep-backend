using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Catalog.Infrastructure.Persistence;
using Modules.Purchases.Application;
using Modules.Purchases.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Catalog → Purchases (mayorista fase 3, D-17/D-18): la confirmación del mayorista encuentra por código, o crea, el producto en
/// el catálogo del distribuidor. Calca <see cref="ShopifyCatalogWriter"/>:
/// <list type="bullet">
/// <item><b>Corre dentro de la transacción del <see cref="PurchasesDbContext"/> del scope</b> (la abre el procesador del inbox;
/// sin ella, <see cref="InvalidOperationException"/>). Cada llamada abre un scope hijo y enrola su <see cref="CatalogDbContext"/> en
/// esa conexión y esa transacción: el producto, la confirmación y el inbox se confirman juntos o no se confirman.</item>
/// <item><b>Un punto de guardado por llamada</b> (ruling X2): en Postgres una sentencia fallida aborta la transacción entera. Si el
/// alta falla (índice único en carrera, nombre o código inválido), se vuelve al punto de guardado y se relanza; quien llama lo
/// registra y la confirmación se guarda igual. La entidad fallida muere con el scope hijo: nada queda rastreado.</item>
/// <item>El producto creado: nombre de la línea recortado a <see cref="Product.NameMaxLength"/> (el código si viene vacío), una
/// variante con el código recortado, sin opciones, categoría ni tasa, activo, <b>sin precio de venta y sin origen externo</b>, y
/// costo sin IVA sólo si es mayor que 0. Firma el sistema (<see cref="Guid.Empty"/>).</item>
/// </list>
/// </summary>
internal sealed class PurchasesCatalogWriter(PurchasesDbContext purchases, IServiceScopeFactory scopes) : IPurchasesCatalogWriter
{
    private const string Savepoint = "purchases_catalog_write";

    // La única variante del producto: la clave sólo empareja borrador y resultado.
    private const string VariantKey = "purchase-order-line";

    public async Task<PurchasesCatalogEntry> EnsureVariantAsync(
        Guid tenantId,
        string code,
        string name,
        string currency,
        decimal? unitCost,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var transaction = purchases.Database.CurrentTransaction
            ?? throw new InvalidOperationException("A purchases catalog write must run inside the purchases transaction.");

        await transaction.CreateSavepointAsync(Savepoint, cancellationToken);
        try
        {
            var entry = await WriteAsync(transaction, tenantId, code.Trim(), name, currency, unitCost, now, cancellationToken);
            await transaction.ReleaseSavepointAsync(Savepoint, cancellationToken);
            return entry;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Sin el token: la vuelta al punto de guardado es lo que deja usable la transacción para la confirmación.
            await transaction.RollbackToSavepointAsync(Savepoint, CancellationToken.None);
            throw;
        }
    }

    private async Task<PurchasesCatalogEntry> WriteAsync(
        IDbContextTransaction transaction,
        Guid tenantId,
        string code,
        string name,
        string currency,
        decimal? unitCost,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        catalog.Database.SetDbConnection(purchases.Database.GetDbConnection(), contextOwnsConnection: false);
        await catalog.Database.UseTransactionAsync(transaction.GetDbTransaction(), cancellationToken);
        var writer = scope.ServiceProvider.GetRequiredService<IExternalProductWriter>();

        if (await writer.FindVariantIdByCodeAsync(tenantId, code, cancellationToken) is { } existing)
        {
            return new PurchasesCatalogEntry(existing, Created: false, ProductId: null);
        }

        var productName = string.IsNullOrWhiteSpace(name) ? code : name.Trim();
        if (productName.Length > Product.NameMaxLength)
        {
            productName = productName[..Product.NameMaxLength].TrimEnd();
        }

        var result = await writer.CreateAsync(
            new ExternalProductDraft(
                tenantId,
                Source: null,
                productName,
                IsActive: true,
                TaxRateId: null,
                currency,
                Options: [],
                [new ExternalVariantDraft(VariantKey, code, Barcode: null, OptionValues: [], Price: null, Cost: unitCost > 0m ? unitCost : null)],
                ActorId: Guid.Empty,
                now),
            cancellationToken);
        return new PurchasesCatalogEntry(result.VariantIdsByKey[VariantKey], Created: true, result.ProductId);
    }
}
