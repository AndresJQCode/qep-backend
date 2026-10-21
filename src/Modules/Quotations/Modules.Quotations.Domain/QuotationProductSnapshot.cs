using BuildingBlocks.Domain;

namespace Modules.Quotations.Domain;

/// <summary>
/// El código y el nombre visible que tenía una variante del catálogo en el momento en que la
/// cotización salió del borrador (owner, 2026-09-26; por variante desde el spec 2026-10-20 §4). Lo
/// arma la aplicación con <c>IQuotationProductLookup</c> —el dominio no ve Catalog— y lo baja a
/// <see cref="Quotation.Send"/>, <see cref="Quotation.ConvertToOrder"/> o
/// <see cref="Quotation.CaptureProductSnapshotsAfterConversion"/>.
/// </summary>
public sealed record QuotationProductSnapshot(string Code, string Name)
{
    /// <summary>
    /// Los largos de <c>Catalog.Domain.Product.NameMaxLength</c> y <c>CodeMaxLength</c>, copiados
    /// y no referenciados: este assembly no puede depender de Catalog. Si allá crecen, acá hay que
    /// crecerlos a mano y con migración, o un producto válido allá no cabría en la columna al enviar.
    /// </summary>
    public const int CodeMaxLength = 60;

    public const int NameMaxLength = 200;

    /// <summary>
    /// El nombre visible, recortado a <see cref="NameMaxLength"/> con «…» al final si no cabe.
    ///
    /// Con variantes el nombre visible es «{producto} – {v1} / {v2} / {v3}» (spec 2026-10-20 §3.4):
    /// el producto llega a 200 y cada valor de opción a 60, así que puede pasar de la columna
    /// <c>product_name</c>. Se decidió recortar y no ensanchar la columna (T10, 2026-10-21): un
    /// nombre de más de 200 caracteres ya no cabe entero en el PDF ni en la celda del Excel, y
    /// ensanchar obliga a crecer también <c>pos.sale_lines</c> y cada consumidor que copia el largo.
    /// El recorte vive acá, en el único constructor, para que ningún camino (envío, conversión,
    /// pedido manual, pedido externo) pueda guardar un nombre que la base rechazaría con un 500.
    /// Sin <c>init</c> a propósito: un <c>with</c> no puede saltarse el recorte.
    /// </summary>
    public string Name { get; } = Fit(Name);

    /// <summary>Ningún producto resuelto: todas las líneas se quedan como estaban.</summary>
    public static readonly IReadOnlyDictionary<Guid, QuotationProductSnapshot> None =
        new Dictionary<Guid, QuotationProductSnapshot>();

    /// <summary>Recorta a <see cref="NameMaxLength"/> con la regla compartida
    /// (<see cref="SnapshotText.Fit"/>), la misma que usa Pos en <c>pos.sale_lines</c>.</summary>
    public static string Fit(string name) => SnapshotText.Fit(name, NameMaxLength);
}
