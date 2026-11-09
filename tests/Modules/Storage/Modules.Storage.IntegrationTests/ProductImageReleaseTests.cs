using System.Net;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Storage.Infrastructure.ProductImages;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// La purga de las fotos de producto que Catalog soltó (spec 2026-11-08): la foto pierde su original, sus
/// variantes y sus copias públicas, queda Purged y auditada. Se respeta la que otro módulo todavía usa,
/// cada mensaje se aplica una vez, un borrado fallido no guarda nada, un mensaje mal formado se marca sin
/// frenar a los demás y nada que no sea una foto de producto del tenant del evento se toca.
/// </summary>
public sealed class ProductImageReleaseTests
{
    [Fact]
    public async Task AReleasedImageLosesItsObjectAndVariantsAndIsPurged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var image = await ProductImageAsync(client, factory);
        var variantKeys = await VariantKeysAsync(database.GetConnectionString(), image.FileId);
        Assert.NotEmpty(variantKeys);
        var messageId = await AddImagesReleasedEventAsync(factory, TenantId, image.FileId);

        Assert.Equal(1, await RunImageReleaseAsync(factory));

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), image.FileId)).Status);
        Assert.False(factory.ObjectStorage.Exists(await FinalKeyAsync(database.GetConnectionString(), image.FileId)));
        Assert.All(variantKeys, key => Assert.False(factory.ObjectStorage.Exists(key)));
        Assert.True(await IsProcessedByImageReleaseAsync(factory, messageId));
        var audit = Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
        Assert.Equal(image.FileId.ToString(), audit.ResourceId);
        Assert.Equal("file", audit.ResourceType);
        Assert.Equal("product_image_released", audit.Outcome);
        Assert.Equal("System", audit.ActorType);
        Assert.Equal(TenantId, audit.TenantId);
    }

    [Fact]
    public async Task APublishedImageAlsoLosesItsPublicCopies()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var image = await ProductImageAsync(client, factory);
        using (var publisher = CreateClient(factory))
        {
            publisher.DefaultRequestHeaders.Remove("X-Permissions");
            publisher.DefaultRequestHeaders.Add("X-Permissions", "storage.file.publish,storage.file.read");
            using var response = await publisher.PutAsync(
                $"{FilesUrl}/{image.FileId}/publication", content: null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var published = await ReadFileAsync(database.GetConnectionString(), image.FileId);
        var publicKey = Assert.IsType<string>(published.PublicStorageKey);
        var variantKeys = await VariantKeysAsync(database.GetConnectionString(), image.FileId);
        // Las copias públicas son todo lo que hay en el bucket público: el original y una por variante.
        var publicKeys = factory.PublicObjectStorage.Keys.ToArray();
        Assert.Contains(publicKey, publicKeys);
        Assert.Equal(variantKeys.Count + 1, publicKeys.Length);
        await AddImagesReleasedEventAsync(factory, TenantId, image.FileId);

        Assert.Equal(1, await RunImageReleaseAsync(factory));

        var purged = await ReadFileAsync(database.GetConnectionString(), image.FileId);
        Assert.Equal("Purged", purged.Status);
        Assert.Empty(factory.PublicObjectStorage.Keys);
        // Sin URL pública que ya no sirve: la fila queda sin clave pública.
        Assert.Null(purged.PublicStorageKey);
        Assert.All(variantKeys, key => Assert.False(factory.ObjectStorage.Exists(key)));
    }

    [Fact]
    public async Task AnImageStillReferencedIsKept()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var image = await ProductImageAsync(client, factory);
        var variantKeys = await VariantKeysAsync(database.GetConnectionString(), image.FileId);
        factory.FileReferences.Reference(image.FileId);
        var messageId = await AddImagesReleasedEventAsync(factory, TenantId, image.FileId);

        Assert.Equal(1, await RunImageReleaseAsync(factory));

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), image.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(await FinalKeyAsync(database.GetConnectionString(), image.FileId)));
        Assert.All(variantKeys, key => Assert.True(factory.ObjectStorage.Exists(key)));
        Assert.True(await IsProcessedByImageReleaseAsync(factory, messageId));
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
    }

    [Fact]
    public async Task AReleaseIsAppliedOnlyOnce()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var image = await ProductImageAsync(client, factory);
        await AddImagesReleasedEventAsync(factory, TenantId, image.FileId);
        Assert.Equal(1, await RunImageReleaseAsync(factory));
        var other = await ProductImageAsync(client, factory);

        // El mensaje ya está en el inbox: un segundo lote no encuentra nada, ni borra lo que no es suyo.
        Assert.Equal(0, await RunImageReleaseAsync(factory));

        Assert.True(factory.ObjectStorage.Exists(await FinalKeyAsync(database.GetConnectionString(), other.FileId)));
        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), other.FileId)).Status);
        Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
    }

    [Fact]
    public async Task WhenADeleteFailsNothingIsSavedAndTheMessageComesBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var image = await ProductImageAsync(client, factory);
        var messageId = await AddImagesReleasedEventAsync(factory, TenantId, image.FileId);
        factory.ObjectStorage.FailingDeleteKey = await FinalKeyAsync(database.GetConnectionString(), image.FileId);

        Assert.Equal(0, await RunImageReleaseAsync(factory));

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), image.FileId)).Status);
        Assert.False(await IsProcessedByImageReleaseAsync(factory, messageId));
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");

        factory.ObjectStorage.FailingDeleteKey = null;

        Assert.Equal(1, await RunImageReleaseAsync(factory));

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), image.FileId)).Status);
        Assert.False(factory.ObjectStorage.Exists(await FinalKeyAsync(database.GetConnectionString(), image.FileId)));
        Assert.True(await IsProcessedByImageReleaseAsync(factory, messageId));
    }

    [Fact]
    public async Task MalformedMessagesAreMarkedAndDoNotBlockALaterValidMessage()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var image = await ProductImageAsync(client, factory);
        // payload_json es jsonb: un "{" suelto no se puede ni guardar, así que el no-objeto es un arreglo.
        var notJson = await AddImagesReleasedPayloadAsync(factory, "[]");
        var badTenant = await AddImagesReleasedPayloadAsync(factory, """{"tenantId":"x"}""");
        var noFiles = await AddImagesReleasedPayloadAsync(factory, $$"""{"tenantId":"{{TenantId}}"}""");
        var valid = await AddImagesReleasedEventAsync(factory, TenantId, image.FileId);

        Assert.Equal(4, await RunImageReleaseAsync(factory));

        Assert.True(await IsProcessedByImageReleaseAsync(factory, notJson));
        Assert.True(await IsProcessedByImageReleaseAsync(factory, badTenant));
        Assert.True(await IsProcessedByImageReleaseAsync(factory, noFiles));
        Assert.True(await IsProcessedByImageReleaseAsync(factory, valid));
        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), image.FileId)).Status);
        Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
    }

    [Fact]
    public async Task AnImageOfAnotherTenantIsSkipped()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var image = await ProductImageAsync(client, factory);
        var messageId = await AddImagesReleasedEventAsync(factory, Guid.CreateVersion7(), image.FileId);

        Assert.Equal(1, await RunImageReleaseAsync(factory));

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), image.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(await FinalKeyAsync(database.GetConnectionString(), image.FileId)));
        Assert.True(await IsProcessedByImageReleaseAsync(factory, messageId));
    }

    [Fact]
    public async Task ANonProductFileIsNeverPurged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var user = await CreateAvailableAsync(client, factory, "User", "foto.png", "image/png", await PngAsync(1600, 1200));
        var tenant = await CreateAvailableAsync(client, factory, "Tenant", "logo.png", "image/png", await PngAsync(1600, 1200));
        var messageId = await AddImagesReleasedEventAsync(factory, TenantId, user.FileId, tenant.FileId);

        Assert.Equal(1, await RunImageReleaseAsync(factory));

        foreach (var file in new[] { user, tenant })
        {
            Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), file.FileId)).Status);
            Assert.True(factory.ObjectStorage.Exists(await FinalKeyAsync(database.GetConnectionString(), file.FileId)));
            Assert.All(
                await VariantKeysAsync(database.GetConnectionString(), file.FileId),
                key => Assert.True(factory.ObjectStorage.Exists(key)));
        }

        Assert.True(await IsProcessedByImageReleaseAsync(factory, messageId));
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
    }

    // Sin ninguna sonda registrada no se puede saber si un producto usa la foto: no se purga nada y el
    // mensaje queda pendiente, así se aplica cuando haya sondas.
    [Fact]
    public async Task WithoutProbesNothingIsPurgedAndTheMessageWaits()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var image = await ProductImageAsync(client, factory);
        var messageId = await AddImagesReleasedEventAsync(factory, TenantId, image.FileId);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var processor = ActivatorUtilities.CreateInstance<ProductImageReleaseProcessor>(
                scope.ServiceProvider, new List<IFileReferenceProbe>());
            Assert.Equal(0, await processor.ProcessPendingAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), image.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(await FinalKeyAsync(database.GetConnectionString(), image.FileId)));
        Assert.False(await IsProcessedByImageReleaseAsync(factory, messageId));

        Assert.Equal(1, await RunImageReleaseAsync(factory));

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), image.FileId)).Status);
    }

    // Un mensaje con dos fotos: cada una se guarda antes de pasar a la siguiente, así que si el borrado de la
    // segunda falla la primera queda Purged y el reintento no la toca otra vez.
    [Fact]
    public async Task APartialFailureKeepsTheFirstFilePurgedAndTheRetrySkipsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var first = await ProductImageAsync(client, factory);
        var second = await ProductImageAsync(client, factory);
        var firstKey = await FinalKeyAsync(database.GetConnectionString(), first.FileId);
        var secondKey = await FinalKeyAsync(database.GetConnectionString(), second.FileId);
        var messageId = await AddImagesReleasedEventAsync(factory, TenantId, first.FileId, second.FileId);
        factory.ObjectStorage.FailingDeleteKey = secondKey;

        Assert.Equal(0, await RunImageReleaseAsync(factory));

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), first.FileId)).Status);
        Assert.False(factory.ObjectStorage.Exists(firstKey));
        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), second.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(secondKey));
        Assert.False(await IsProcessedByImageReleaseAsync(factory, messageId));
        Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");

        factory.ObjectStorage.FailingDeleteKey = null;

        Assert.Equal(1, await RunImageReleaseAsync(factory));

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), second.FileId)).Status);
        Assert.False(factory.ObjectStorage.Exists(secondKey));
        Assert.True(await IsProcessedByImageReleaseAsync(factory, messageId));
        var audits = (await AuditEventsAsync(factory)).Where(entry => entry.Action == "storage.file.purged").ToArray();
        Assert.Equal(2, audits.Length);
        Assert.Single(audits, entry => entry.ResourceId == first.FileId.ToString());
    }

    // Al completar la subida el original se promueve: la clave final de la base, no la de staging, es la del objeto.
    private static async Task<string> FinalKeyAsync(string connectionString, Guid fileId) =>
        (await ReadFileAsync(connectionString, fileId)).StorageKey;

    private static async Task<UploadedFile> ProductImageAsync(HttpClient client, StorageApiFactory factory) =>
        await CreateAvailableAsync(client, factory, "Product", "foto.png", "image/png", await PngAsync(1600, 1200));
}
