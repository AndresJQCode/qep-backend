namespace Modules.Quotations.Domain;

/// <summary>
/// El código y el nombre que tenía un producto del catálogo en el momento en que la cotización
/// salió del borrador (owner, 2026-09-26). Lo arma la aplicación con <c>IQuotationProductLookup</c>
/// —el dominio no ve Catalog— y lo baja a <see cref="Quotation.Send"/>,
/// <see cref="Quotation.ConvertToOrder"/> o <see cref="Quotation.CaptureProductSnapshotsAfterConversion"/>.
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

    /// <summary>Ningún producto resuelto: todas las líneas se quedan como estaban.</summary>
    public static readonly IReadOnlyDictionary<Guid, QuotationProductSnapshot> None =
        new Dictionary<Guid, QuotationProductSnapshot>();
}
