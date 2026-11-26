using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>
/// La copia del original y sus variantes al bucket público, con rollback si una copia falla
/// (decisión 5 del spec 2026-09-19). Extraído de `PublishFileHandler`/`SoftDeleteFileHandler`
/// para que el adaptador de logo del tenant (Bootstrapper) lo use sin pasar por esos handlers.
/// </summary>
public sealed class FilePublicationTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PublishRejectsWhenThePublicBucketIsNotConfigured()
    {
        var resource = AvailableImage();
        var publication = new FilePublication(new UnconfiguredPublicObjectStorage(), new FixedClock(Now));

        var error = await Assert.ThrowsAsync<StorageDomainException>(() =>
            publication.PublishAsync(resource, TestContext.Current.CancellationToken));

        Assert.Equal("storage.public.not_configured", error.Code);
        Assert.Null(resource.PublicStorageKey);
    }

    [Fact]
    public async Task PublishCopiesTheOriginalAndMarksThePublicKey()
    {
        var resource = AvailableImage();
        var storage = new RecordingPublicObjectStorage();
        var publication = new FilePublication(storage, new FixedClock(Now));

        var publicKey = await publication.PublishAsync(resource, TestContext.Current.CancellationToken);

        Assert.Equal(resource.PublicStorageKey, publicKey);
        Assert.Equal(resource.StorageKey, storage.Copies[publicKey]);
    }

    [Fact]
    public async Task PublishRollsBackTheOriginalCopyWhenAVariantCopyFails()
    {
        var resource = AvailableImageWithVariant();
        var storage = new FailingOnVariantPublicObjectStorage();
        var publication = new FilePublication(storage, new FixedClock(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publication.PublishAsync(resource, TestContext.Current.CancellationToken));

        Assert.Empty(storage.Copies);
    }

    [Fact]
    public async Task UnpublishDeletesTheOriginalAndEachVariantCopy()
    {
        var resource = AvailableImageWithVariant();
        var storage = new RecordingPublicObjectStorage();
        var publication = new FilePublication(storage, new FixedClock(Now));
        var publicKey = await publication.PublishAsync(resource, TestContext.Current.CancellationToken);

        await publication.UnpublishAsync(resource, TestContext.Current.CancellationToken);

        Assert.Contains(publicKey, storage.DeletedKeys);
        Assert.Equal(2, storage.DeletedKeys.Count);
        Assert.Null(resource.PublicStorageKey);
    }

    [Fact]
    public async Task UnpublishWithoutAPublicKeyIsANoOp()
    {
        var resource = AvailableImage();
        var storage = new RecordingPublicObjectStorage();
        var publication = new FilePublication(storage, new FixedClock(Now));

        await publication.UnpublishAsync(resource, TestContext.Current.CancellationToken);

        Assert.Empty(storage.DeletedKeys);
    }

    // Spec 2026-11-17, «Closing the residual gaps»: si el guardado de Storage falla después de copiar, la fila no tiene
    // PublicStorageKey pero las copias existen. La clave es determinista, así que se retira igual.
    [Fact]
    public async Task RemovingWithoutAStoredKeyDeletesTheComputedKeyAndVariants()
    {
        var resource = AvailableImageWithVariant();
        var storage = new RecordingPublicObjectStorage();
        var publication = new FilePublication(storage, new FixedClock(Now));
        // StorageKey es internal: la clave se escribe a mano, y así queda fijada (PublicFor y PublicVariantFor).
        var computedKey = $"tenants/{TenantId:N}/media/{resource.Id.Value:N}/original.png";
        var variantKey = $"tenants/{TenantId:N}/media/{resource.Id.Value:N}/variants/thumb.png";

        await publication.RemovePublicCopiesAsync(resource, TestContext.Current.CancellationToken);

        Assert.Equal([computedKey, variantKey], storage.DeletedKeys);
        Assert.Null(resource.PublicStorageKey);
        Assert.Null(resource.PublishedAt);
    }

    [Fact]
    public async Task RemovingWithAStoredKeyDeletesThatKey()
    {
        var resource = AvailableImageWithVariant();
        var storage = new RecordingPublicObjectStorage();
        var publication = new FilePublication(storage, new FixedClock(Now));
        var publicKey = await publication.PublishAsync(resource, TestContext.Current.CancellationToken);

        await publication.RemovePublicCopiesAsync(resource, TestContext.Current.CancellationToken);

        Assert.Equal(publicKey, storage.DeletedKeys[0]);
        Assert.Equal(2, storage.DeletedKeys.Count);
        Assert.Null(resource.PublicStorageKey);
    }

    // Borrar una clave que no existe no falla en R2: repetir el retiro (reintento del outbox, barrido) no rompe nada.
    [Fact]
    public async Task RemovingIsIdempotentWhenTheObjectsAreGone()
    {
        var resource = AvailableImageWithVariant();
        var storage = new MissingObjectsPublicObjectStorage();
        var publication = new FilePublication(storage, new FixedClock(Now));

        await publication.RemovePublicCopiesAsync(resource, TestContext.Current.CancellationToken);
        await publication.RemovePublicCopiesAsync(resource, TestContext.Current.CancellationToken);

        Assert.Equal(4, storage.DeleteCalls);
        Assert.Null(resource.PublicStorageKey);
    }

    private static FileResource AvailableImage()
    {
        var resource = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.NewGuid(), FileOwnerType.User,
            "logo.png", "image/png", 2048, $"staging/tenants/{TenantId:N}/logo", Now);
        resource.CompleteUpload("checksum", 2048, Now);
        resource.Promote($"files/tenants/{TenantId:N}/logo", Now);
        return resource;
    }

    // AddVariant sólo se puede llamar en PendingScan (FileResource.cs:322), así que la variante se
    // agrega antes de Promote, no después como en un AvailableImage() ya publicado.
    private static FileResource AvailableImageWithVariant()
    {
        var resource = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.NewGuid(), FileOwnerType.User,
            "logo.png", "image/png", 2048, $"staging/tenants/{TenantId:N}/logo", Now);
        resource.CompleteUpload("checksum", 2048, Now);
        resource.AddVariant(
            "thumb", $"{resource.StorageKey}/variants/thumb.png", "image/png", 200, 200, 512);
        resource.Promote($"files/tenants/{TenantId:N}/logo", Now);
        return resource;
    }

    private sealed class UnconfiguredPublicObjectStorage : IPublicObjectStorage
    {
        public Task UploadAsync(string publicKey, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken) => Task.CompletedTask;

        public bool IsConfigured => false;

        public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public string GetUrl(string publicKey) => throw new NotSupportedException();

        public Task<PublicObjectPage> ListAsync(
            string prefix, string? continuationToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    // Un bucket sin objetos: borrar no encuentra nada y no falla, como DeleteObject de R2 (204 aunque la clave no exista).
    private sealed class MissingObjectsPublicObjectStorage : IPublicObjectStorage
    {
        public Task UploadAsync(string publicKey, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken) => Task.CompletedTask;

        public int DeleteCalls { get; private set; }

        public bool IsConfigured => true;

        public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string publicKey, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string publicKey, CancellationToken cancellationToken) => Task.FromResult(false);

        public string GetUrl(string publicKey) => throw new NotSupportedException();

        public Task<PublicObjectPage> ListAsync(
            string prefix, string? continuationToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    // Copia el original y falla en la primera variante, para ejercer el rollback: la copia del
    // original queda en `Copies` hasta el catch, que la borra por su cuenta.
    private sealed class FailingOnVariantPublicObjectStorage : IPublicObjectStorage
    {
        public Task UploadAsync(string publicKey, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken) => Task.CompletedTask;

        public Dictionary<string, string> Copies { get; } = new(StringComparer.Ordinal);

        public bool IsConfigured => true;

        public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken)
        {
            if (privateKey.Contains("/variants/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated failure copying a variant.");
            }

            Copies[publicKey] = privateKey;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string publicKey, CancellationToken cancellationToken)
        {
            Copies.Remove(publicKey);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public string GetUrl(string publicKey) => throw new NotSupportedException();

        public Task<PublicObjectPage> ListAsync(
            string prefix, string? continuationToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
