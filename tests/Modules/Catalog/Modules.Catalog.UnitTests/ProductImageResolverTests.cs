using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// CAT-05a — las tres reglas de `ImageFileId`.
///
/// Se prueban acá, con un doble del puerto, y no sólo por integración: las reglas son de
/// `catalog` y no dependen de PostgreSQL ni de `Storage`. La integración verifica que el cableado
/// exista; esto verifica qué decide cada rama.
/// </summary>
public sealed class ProductImageResolverTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid OtherTenantId = Guid.CreateVersion7();

    // CA-CAT-05-06: sigue siendo opcional. Sin imagen no se le pregunta nada al puerto.
    [Fact]
    public async Task ANullImageIsAcceptedWithoutAskingTheLookup()
    {
        var lookup = new FailingLookup();

        var resolved = await ProductImageResolver.ResolveAsync(
            lookup, TenantId, imageFileId: null, TestContext.Current.CancellationToken);

        Assert.Null(resolved);
        Assert.False(lookup.WasCalled);
    }

    // CA-CAT-05-02
    [Fact]
    public async Task AnUnknownImageIsRejected()
    {
        var error = await Assert.ThrowsAsync<CatalogDomainException>(async () =>
            await ProductImageResolver.ResolveAsync(
                new StubLookup(null), TenantId, Guid.CreateVersion7(),
                TestContext.Current.CancellationToken));

        Assert.Equal("catalog.product.image_not_found", error.Code);
    }

    /// <summary>
    /// CA-CAT-05-01 — la fuga entre tenants, que es la razón de existir del slice.
    ///
    /// **Devuelve el mismo código que el archivo inexistente a propósito.** Distinguirlos le
    /// confirmaría al llamador que el id existe en otro tenant, que es justo lo que la frontera
    /// esconde. Mismo razonamiento que `ProductTaxRateResolver`.
    /// </summary>
    [Fact]
    public async Task AnImageFromAnotherTenantIsRejectedWithTheSameCodeAsAnUnknownOne()
    {
        var foreign = new ProductImageRef(
            Guid.CreateVersion7(), OtherTenantId, "image/png", IsAvailable: true, PublicUrl: null);

        var error = await Assert.ThrowsAsync<CatalogDomainException>(async () =>
            await ProductImageResolver.ResolveAsync(
                new StubLookup(foreign), TenantId, foreign.FileId,
                TestContext.Current.CancellationToken));

        Assert.Equal("catalog.product.image_not_found", error.Code);
    }

    // CA-CAT-05-03: una portada que todavía no se subió no es una portada.
    [Fact]
    public async Task AnImageThatIsNotAvailableYetIsRejected()
    {
        var pending = new ProductImageRef(
            Guid.CreateVersion7(), TenantId, "image/png", IsAvailable: false, PublicUrl: null);

        var error = await Assert.ThrowsAsync<CatalogDomainException>(async () =>
            await ProductImageResolver.ResolveAsync(
                new StubLookup(pending), TenantId, pending.FileId,
                TestContext.Current.CancellationToken));

        Assert.Equal("catalog.product.image_not_available", error.Code);
    }

    // CA-CAT-05-04. La regla es de catalog, no de FileUploadPolicy: esa lista blanca es de
    // storage y mezcla PDF y Office con las imagenes.
    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/vnd.ms-excel")]
    [InlineData("text/plain")]
    public async Task AFileThatIsNotAnImageIsRejected(string mimeType)
    {
        var document = new ProductImageRef(
            Guid.CreateVersion7(), TenantId, mimeType, IsAvailable: true, PublicUrl: null);

        var error = await Assert.ThrowsAsync<CatalogDomainException>(async () =>
            await ProductImageResolver.ResolveAsync(
                new StubLookup(document), TenantId, document.FileId,
                TestContext.Current.CancellationToken));

        Assert.Equal("catalog.product.image_not_an_image", error.Code);
    }

    // CA-CAT-05-05
    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    [InlineData("IMAGE/PNG")]
    public async Task AnAvailableImageFromTheSameTenantIsAccepted(string mimeType)
    {
        var image = new ProductImageRef(
            Guid.CreateVersion7(), TenantId, mimeType, IsAvailable: true, PublicUrl: null);

        var resolved = await ProductImageResolver.ResolveAsync(
            new StubLookup(image), TenantId, image.FileId, TestContext.Current.CancellationToken);

        // Devuelve la referencia entera, no sólo el id: quien escribe ya pagó la consulta y arma
        // la respuesta con su PublicUrl sin volver a preguntar (CAT-05b).
        Assert.Equal(image, resolved);
    }

    private sealed class StubLookup(ProductImageRef? result) : IProductImageLookup
    {
        public Task<ProductImageRef?> FindAsync(
            Guid fileId, CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public Task<IReadOnlyDictionary<Guid, ProductImageRef>> FindManyAsync(
            IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ProductImageRef>>(
                new Dictionary<Guid, ProductImageRef>());
    }

    // Si el resolver le pregunta al puerto por una imagen nula, este doble lo delata.
    private sealed class FailingLookup : IProductImageLookup
    {
        public bool WasCalled { get; private set; }

        public Task<ProductImageRef?> FindAsync(
            Guid fileId, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new InvalidOperationException("The lookup must not be called for a null image.");
        }

        public Task<IReadOnlyDictionary<Guid, ProductImageRef>> FindManyAsync(
            IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new InvalidOperationException("The lookup must not be called for a null image.");
        }
    }
}
