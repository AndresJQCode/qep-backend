using Microsoft.Extensions.DependencyInjection;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// La lectura en lote de <see cref="IFileResourceRepository"/> contra Postgres real: la usa el
/// detalle de un pedido para resolver las portadas de todos sus productos con una sola consulta.
/// Tiene que traer lo mismo que GetAsync —variantes incluidas, sin filtro de tenant— para que
/// cambiar una por la otra no cambie lo que ve la pantalla.
/// </summary>
public sealed class FileResourceRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListByIdsReturnsTheRequestedResourcesWithTheirVariantsAndIgnoresUnknownIds()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var withVariant = ImageWithThumbnail(TenantId);
        // De otro tenant a propósito: el repositorio no filtra, eso lo decide quien lo llama.
        var otherTenant = ImageWithThumbnail(Guid.CreateVersion7());
        var notRequested = ImageWithThumbnail(TenantId);
        await SeedAsync(factory, withVariant, otherTenant, notRequested);

        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFileResourceRepository>();
        var found = await repository.ListByIdsAsync(
            [withVariant.Id, otherTenant.Id, FileResourceId.New()],
            TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { withVariant.Id, otherTenant.Id }.OrderBy(id => id.Value),
            found.Select(resource => resource.Id).OrderBy(id => id.Value));
        var loaded = Assert.Single(found, resource => resource.Id == withVariant.Id);
        var variant = Assert.Single(loaded.Variants);
        Assert.Equal("thumbnail", variant.Name);
        Assert.Equal(otherTenant.TenantId, found.Single(resource => resource.Id == otherTenant.Id).TenantId);
    }

    [Fact]
    public async Task ListByIdsWithoutIdsReturnsEmpty()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        await SeedAsync(factory, ImageWithThumbnail(TenantId));

        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFileResourceRepository>();
        var found = await repository.ListByIdsAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(found);
    }

    private static FileResource ImageWithThumbnail(Guid tenantId)
    {
        var image = FileResource.CreatePendingUpload(
            FileResourceId.New(), tenantId, Guid.CreateVersion7(), FileOwnerType.User, "portada.png",
            "image/png", 1024, $"staging/{Guid.CreateVersion7():N}", Now);
        image.CompleteUpload("checksum", 1024, Now);
        image.AddVariant(
            "thumbnail", $"files/{Guid.CreateVersion7():N}/thumbnail.webp", "image/webp", 64, 64, 256);
        image.MarkClean(Now);
        return image;
    }

    // Por el repositorio y su unidad de trabajo, en un scope aparte: la lectura de la prueba sale de
    // otro DbContext, así que las variantes llegan de la base y no del change tracker.
    private static async Task SeedAsync(StorageApiFactory factory, params FileResource[] resources)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFileResourceRepository>();
        foreach (var resource in resources)
        {
            repository.Add(resource);
        }

        await scope.ServiceProvider.GetRequiredService<IStorageUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
