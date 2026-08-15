namespace Modules.Catalog.Application;

/// <summary>
/// Lo que `catalog` necesita saber de un archivo para decidir si sirve como portada de producto.
/// **Es un puerto de `catalog`, no un tipo de `Storage`.**
///
/// La forma obvia habría sido que <c>Modules.Catalog.Application</c> referenciara
/// <c>Modules.Storage.Application</c> y llamara a <c>IFileResourceRepository</c> — tiene
/// precedente, porque `Storage` referencia a `Tenancy` por <c>IExecutionContext</c>.
///
/// Se hace al revés a propósito (decisión 1 del spec de `CAT-05`): el acoplamiento entre dos
/// módulos de negocio vive en el composition root, que es su trabajo, y no adentro de un módulo.
/// `catalog` compila sin `storage`, y `CatalogLayerTests` lo verifica para que la decisión sea
/// una regla y no un comentario.
/// </summary>
public interface IProductImageLookup
{
    /// <summary>
    /// Trae el archivo por id, **sin filtrar por tenant**. Filtrar acá sería más cómodo y está
    /// mal: la comprobación de tenant es la garantía que este slice existe para dar, y una
    /// garantía escondida en el adaptador no se puede probar contra el mecanismo ausente. Ver
    /// <see cref="ProductImageResolver"/>.
    /// </summary>
    Task<ProductImageRef?> FindAsync(Guid fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Los archivos de un lote de ids, para que el listado de productos resuelva sus URLs sin
    /// una vuelta por producto. Los ids que no existen simplemente no aparecen en el resultado.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ProductImageRef>> FindManyAsync(
        IReadOnlyCollection<Guid> fileIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// La proyección de un archivo que `catalog` entiende. No es el agregado de `Storage`: lleva
/// sólo lo que hace falta para las tres reglas y para exponer la URL.
/// </summary>
/// <param name="FileId">Id del archivo en `Storage`.</param>
/// <param name="TenantId">A qué tenant pertenece. Lo compara el resolver, no el adaptador.</param>
/// <param name="MimeType">Tipo declarado. `catalog` decide qué es imagen, no `Storage`.</param>
/// <param name="IsAvailable">
/// Si terminó su ciclo de subida. Un archivo en `PendingUpload` o en cuarentena no es una portada.
/// </param>
/// <param name="PublicUrl">La URL pública si fue publicado; <c>null</c> si no.</param>
public sealed record ProductImageRef(
    Guid FileId,
    Guid TenantId,
    string MimeType,
    bool IsAvailable,
    string? PublicUrl);
