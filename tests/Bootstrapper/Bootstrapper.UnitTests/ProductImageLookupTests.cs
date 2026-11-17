using Modules.Storage.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Cómo resuelve el composition root las portadas de varios productos de una vez: un detalle de
/// pedido con N productos con imagen no puede costar N consultas a Storage.
/// </summary>
public sealed class ProductImageLookupTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FindManyReadsEveryImageInASingleBatch()
    {
        var first = AvailableImage();
        var second = AvailableImage();
        var repository = new InMemoryFileResourceRepository(first, second);
        var lookup = new ProductImageLookup(repository, new RecordingPublicObjectStorage());

        var found = await lookup.FindManyAsync(
            [first.Id.Value, second.Id.Value, first.Id.Value, Guid.CreateVersion7()],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, repository.ListByIdsCalls);
        Assert.Equal(0, repository.GetCalls);
        Assert.Equal(2, found.Count);
        // El dato crudo, tenant incluido: la regla de tenant es de ProductImageResolver.
        Assert.Equal(TenantId, found[first.Id.Value].TenantId);
        Assert.True(found[second.Id.Value].IsAvailable);
    }

    [Fact]
    public async Task FindManyWithoutIdsDoesNotReachStorage()
    {
        var repository = new InMemoryFileResourceRepository();
        var lookup = new ProductImageLookup(repository, new RecordingPublicObjectStorage());

        var found = await lookup.FindManyAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(found);
        Assert.Equal(0, repository.ListByIdsCalls);
        Assert.Equal(0, repository.GetCalls);
    }

    // Fix round 1 del Bloque D: un adjunto del formulario público de Soporte no puede ser foto de un producto.
    [Fact]
    public async Task ASupportAttachmentIsNotAProductImage()
    {
        var attachment = AvailableImage(FileOwnerType.SupportTicket);
        var image = AvailableImage();
        var lookup = new ProductImageLookup(
            new InMemoryFileResourceRepository(attachment, image), new RecordingPublicObjectStorage());

        Assert.Null(await lookup.FindAsync(attachment.Id.Value, TestContext.Current.CancellationToken));
        var found = await lookup.FindManyAsync([attachment.Id.Value, image.Id.Value], TestContext.Current.CancellationToken);
        Assert.Equal([image.Id.Value], found.Keys);
    }

    private static FileResource AvailableImage(FileOwnerType ownerType = FileOwnerType.User)
    {
        var image = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), ownerType, "portada.png",
            "image/png", 1024, $"staging/{Guid.CreateVersion7():N}", Now);
        image.CompleteUpload("checksum", 1024, Now);
        image.MarkClean(Now);
        return image;
    }
}
