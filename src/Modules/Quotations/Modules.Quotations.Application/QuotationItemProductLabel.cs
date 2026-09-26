using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// La regla de qué código y qué nombre de producto muestra una línea, en un solo lugar (owner,
/// 2026-09-26): el congelado en <see cref="QuotationItem.ProductCode"/> /
/// <see cref="QuotationItem.ProductName"/> si lo tiene —la cotización ya salió del borrador—, y si
/// no, el del catálogo de hoy. Sin ninguno de los dos, vacío: el producto se borró y la línea tiene
/// que poder leerse igual.
///
/// La usan la respuesta de cotización y pedido (y con ella el PDF, que sale de esa respuesta) y el
/// Excel de pedidos. Si cada uno aplicara la regla por su cuenta, tarde o temprano el documento que
/// recibe el cliente diría otra cosa que la pantalla.
/// </summary>
public static class QuotationItemProductLabel
{
    public static string CodeOf(string? snapshot, QuotationProductRef? live) =>
        snapshot ?? live?.Code ?? string.Empty;

    public static string NameOf(string? snapshot, QuotationProductRef? live) =>
        snapshot ?? live?.Name ?? string.Empty;

    /// <summary>
    /// El catálogo de hoy para las líneas que todavía no congelaron su producto, listo para
    /// <see cref="Quotation.Send"/>, <see cref="Quotation.ConvertToOrder"/> o
    /// <see cref="Quotation.CaptureProductSnapshotsAfterConversion"/>. Las ya congeladas no se piden:
    /// el dominio no las va a pisar. Un producto que el catálogo no devuelve no viene, y esa línea
    /// se queda sin snapshot.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, QuotationProductSnapshot>> ResolveMissingAsync(
        IQuotationProductLookup productLookup,
        Guid tenantId,
        Quotation quotation,
        CancellationToken cancellationToken)
    {
        var missing = quotation.Items
            .Where(item => item.ProductCode is null)
            .Select(item => item.ProductId)
            .Distinct()
            .ToArray();
        if (missing.Length == 0)
        {
            return QuotationProductSnapshot.None;
        }

        var products = await productLookup.FindManyAsync(tenantId, missing, cancellationToken);
        return products.ToDictionary(
            entry => entry.Key,
            entry => new QuotationProductSnapshot(entry.Value.Code, entry.Value.Name));
    }
}
