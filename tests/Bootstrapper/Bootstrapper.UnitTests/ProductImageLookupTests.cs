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

    private static FileResource AvailableImage()
    {
        var image = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.User, "portada.png",
            "image/png", 1024, $"staging/{Guid.CreateVersion7():N}", Now);
        image.CompleteUpload("checksum", 1024, Now);
        image.MarkClean(Now);
        return image;
    }
}
