using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Limpia las cotizaciones cuando el catálogo borra variantes (spec 2026-11-08): congela el código y el
/// nombre en las líneas que quedan y saca las líneas de los borradores.
/// </summary>
public interface IQuotationVariantDeletion
{
    /// <summary>
    /// Corre dentro de la transacción que le abre quien llama (el borrado del producto, por
    /// <c>Bootstrapper</c>): no abre ni confirma ninguna, y guarda con el unit of work del módulo, que se suma a
    /// ella. Se invoca **antes** de que el catálogo borre las variantes, porque necesita su código y su nombre.
    /// </summary>
    Task ApplyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> variantIds,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Puerto hacia los datos de cotizaciones para el borrado de variantes. Lo implementa Infrastructure: Application
/// no referencia EF Core.
/// </summary>
public interface IDeletedVariantLineStore
{
    /// <summary>Completa <c>product_code</c> y <c>product_name</c> de las líneas de esas variantes que todavía no
    /// los tienen (las de cotizaciones que no han salido del borrador). Devuelve cuántas líneas tocó.</summary>
    Task<int> BackfillProductSnapshotsAsync(
        Guid tenantId,
        IReadOnlyDictionary<Guid, QuotationProductSnapshot> snapshots,
        CancellationToken cancellationToken);

    /// <summary>Las cotizaciones en borrador que tienen líneas de esas variantes, como agregados **rastreados** y
    /// con sus líneas y partes cargadas.</summary>
    Task<IReadOnlyList<Quotation>> ListDraftsWithVariantsAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken);
}

public sealed class QuotationVariantDeletion(
    IQuotationProductLookup productLookup,
    IDeletedVariantLineStore lineStore,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationSettingsStore settingsStore,
    ITenantPriceRounding priceRounding,
    IQuotationsUnitOfWork unitOfWork) : IQuotationVariantDeletion
{
    public async Task ApplyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> variantIds,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return;
        }

        // Primero el congelado (P1): después de borrar la variante ya no hay de dónde sacar código ni nombre. Una
        // variante que el catálogo no devuelve no entra en el diccionario y sus líneas se quedan como están.
        var variants = await productLookup.FindManyAsync(tenantId, variantIds, cancellationToken);
        var snapshots = variants.ToDictionary(
            pair => pair.Key,
            pair => new QuotationProductSnapshot(pair.Value.Code, pair.Value.Name));
        await lineStore.BackfillProductSnapshotsAsync(tenantId, snapshots, cancellationToken);

        var drafts = await lineStore.ListDraftsWithVariantsAsync(tenantId, variantIds, cancellationToken);
        if (drafts.Count == 0)
        {
            return;
        }

        var deleted = variantIds.ToHashSet();
        foreach (var draft in drafts)
        {
            draft.RemoveItemsOfDeletedVariants(deleted, occurredAt);
            await QuotationPricingRecalculation.ApplyAsync(
                pricingLookup, settingsStore, priceRounding, tenantId, draft, occurredAt, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
