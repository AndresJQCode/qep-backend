using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// El adaptador de Tenancy sobre Storage (decisión 4 del spec 2026-09-19): valida el archivo con
/// las reglas del logo (tipo, tamaño, dueño) y usa <see cref="FilePublication"/> para copiarlo al
/// bucket público, sin pasar por los handlers de Storage ni por sus permisos.
/// </summary>
public sealed class TenantLogoStorageTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly RecordingStorageAuditPublisher _auditPublisher = new();
    private readonly CountingStorageUnitOfWork _storageUnitOfWork = new();

    [Fact]
    public async Task PublishRejectsAFileFromAnotherTenant()
    {
        var file = AvailableLogo(Guid.CreateVersion7(), "image/png");
        var storage = NewStorage(file, new RecordingPublicObjectStorage());

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.logo.file_not_found", error.Code);
    }

    [Fact]
    public async Task PublishRejectsAMissingFile()
    {
        var storage = NewStorage(resources: [], new RecordingPublicObjectStorage());

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            storage.PublishAsync(TenantId, Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.logo.file_not_found", error.Code);
    }

    [Fact]
    public async Task PublishRejectsAFileOwnedByAnotherEntity()
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.NewGuid(), FileOwnerType.Product,
            "logo.png", "image/png", 2048, $"staging/tenants/{TenantId:N}/logo", Now);
        file.CompleteUpload("checksum", 2048, Now);
        file.Promote($"files/tenants/{TenantId:N}/logo", Now);
        var storage = NewStorage(file, new RecordingPublicObjectStorage());

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.logo.not_owned", error.Code);
    }

    [Fact]
    public async Task PublishRejectsAFileThatIsNotAvailableYet()
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, TenantId, FileOwnerType.Tenant,
            "logo.png", "image/png", 2048, $"staging/tenants/{TenantId:N}/logo", Now);
        var storage = NewStorage(file, new RecordingPublicObjectStorage());

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.logo.not_available", error.Code);
    }

    [Fact]
    public async Task PublishRejectsANonImage()
    {
        var file = AvailableLogo(TenantId, "application/pdf", "logo.pdf");
        var storage = NewStorage(file, new RecordingPublicObjectStorage());

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.logo.not_image", error.Code);
    }

    [Fact]
    public async Task PublishRejectsAFileOverTwoMebibytes()
    {
        var file = AvailableLogo(TenantId, "image/png", sizeBytes: (2 * 1024 * 1024) + 1);
        var storage = NewStorage(file, new RecordingPublicObjectStorage());

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.logo.too_large", error.Code);
    }

    [Fact]
    public async Task PublishCopiesUnderTenantsMediaAndMarksThePublicKey()
    {
        var file = AvailableLogo(TenantId, "image/png");
        var publicStorage = new RecordingPublicObjectStorage();
        var storage = NewStorage(file, publicStorage);

        var publication = await storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        Assert.StartsWith(
            $"tenants/{TenantId:N}/media/{file.Id.Value:N}/", publication.PublicKey, StringComparison.Ordinal);
        Assert.Equal(file.StorageKey, publicStorage.Copies[publication.PublicKey]);
        Assert.Equal(publication.PublicKey, file.PublicStorageKey);
        // La publicación se audita y se commitea en Storage, antes del commit de Tenancy.
        Assert.Equal(["storage.file.published"], _auditPublisher.Actions);
        Assert.Equal(1, _storageUnitOfWork.Saves);
    }

    [Fact]
    public async Task UnpublishDeletesTheCopyAndLeavesTheResourceDeleted()
    {
        var file = AvailableLogo(TenantId, "image/png");
        var publicStorage = new RecordingPublicObjectStorage();
        var storage = NewStorage(file, publicStorage);
        await storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        await storage.UnpublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotEmpty(publicStorage.DeletedKeys);
        Assert.Equal(FileResourceStatus.Deleted, file.Status);
        Assert.Equal(["storage.file.published", "storage.file.deleted"], _auditPublisher.Actions);
        Assert.Equal(2, _storageUnitOfWork.Saves);
    }

    // Revalidar el tenant en la frontera: un id de otro tenant no se toca, ni se confirma que
    // existe (vuelve sin error, igual que un archivo que no existe).
    [Fact]
    public async Task UnpublishLeavesAnotherTenantsFileUntouched()
    {
        var otherTenantId = Guid.CreateVersion7();
        var file = AvailableLogo(otherTenantId, "image/png");
        var publicStorage = new RecordingPublicObjectStorage();
        await new FilePublication(publicStorage, new FixedClock(Now))
            .PublishAsync(file, TestContext.Current.CancellationToken);

        await NewStorage(file, publicStorage)
            .UnpublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        Assert.Empty(publicStorage.DeletedKeys);
        Assert.Equal(FileResourceStatus.Available, file.Status);
        Assert.Equal(0, _storageUnitOfWork.Saves);
    }

    // Un archivo del tenant que no es su logo (otro dueño, p. ej. un producto) no se retira por
    // este camino, que sólo autoriza tenancy.settings.update.
    [Fact]
    public async Task UnpublishLeavesAFileOfAnotherOwnerTypeUntouched()
    {
        var file = AvailableFile(TenantId, Guid.CreateVersion7(), FileOwnerType.Product);
        var publicStorage = new RecordingPublicObjectStorage();
        await new FilePublication(publicStorage, new FixedClock(Now))
            .PublishAsync(file, TestContext.Current.CancellationToken);

        await NewStorage(file, publicStorage)
            .UnpublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        Assert.Empty(publicStorage.DeletedKeys);
        Assert.Equal(FileResourceStatus.Available, file.Status);
        Assert.Equal(0, _storageUnitOfWork.Saves);
    }

    [Fact]
    public async Task UnpublishLeavesATenantFileOfAnotherOwnerUntouched()
    {
        var file = AvailableFile(TenantId, Guid.CreateVersion7(), FileOwnerType.Tenant);
        var publicStorage = new RecordingPublicObjectStorage();
        await new FilePublication(publicStorage, new FixedClock(Now))
            .PublishAsync(file, TestContext.Current.CancellationToken);

        await NewStorage(file, publicStorage)
            .UnpublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        Assert.Empty(publicStorage.DeletedKeys);
        Assert.Equal(FileResourceStatus.Available, file.Status);
        Assert.Equal(0, _storageUnitOfWork.Saves);
    }

    // El paso 8 de SetTenantLogoHandler depende de esto: retirar el logo viejo es mejor
    // esfuerzo, y una falla del bucket público no puede fallar el request que ya commiteó.
    [Fact]
    public async Task TryUnpublishSwallowsAStorageFailure()
    {
        var file = AvailableLogo(TenantId, "image/png");
        var publicStorage = new RecordingPublicObjectStorage();
        var storage = NewStorage(file, publicStorage);
        await storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);
        publicStorage.DeleteFailure = new InvalidOperationException("R2 down");

        await storage.TryUnpublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        Assert.Equal(FileResourceStatus.Available, file.Status);
        Assert.Equal(["storage.file.published"], _auditPublisher.Actions);
        Assert.Equal(1, _storageUnitOfWork.Saves);
    }

    [Fact]
    public async Task UnpublishOnAnAlreadyDeletedResourceDoesNotThrow()
    {
        var file = AvailableLogo(TenantId, "image/png");
        var publicStorage = new RecordingPublicObjectStorage();
        var storage = NewStorage(file, publicStorage);
        await storage.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);
        await storage.UnpublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        // Segunda vez, ya Deleted: no debe lanzar (decisión 8 del spec — el commit de Tenancy
        // puede fallar después del primer retiro y quien reintente vuelve a llamar acá).
        await storage.UnpublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void GetUrlReturnsNullWithoutAConfiguredBucket()
    {
        var storage = NewStorage(resources: [], new UnconfiguredPublicObjectStorage());

        Assert.Null(storage.GetUrl("tenants/x/media/y/original.png"));
    }

    private static FileResource AvailableLogo(
        Guid tenantId, string mimeType, string name = "logo.png", long sizeBytes = 2048) =>
        AvailableFile(tenantId, tenantId, FileOwnerType.Tenant, mimeType, name, sizeBytes);

    private static FileResource AvailableFile(
        Guid tenantId,
        Guid ownerId,
        FileOwnerType ownerType,
        string mimeType = "image/png",
        string name = "logo.png",
        long sizeBytes = 2048)
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), tenantId, ownerId, ownerType,
            name, mimeType, sizeBytes, $"staging/tenants/{tenantId:N}/logo", Now);
        file.CompleteUpload("checksum", sizeBytes, Now);
        file.MarkClean(Now);
        return file;
    }

    private TenantLogoStorage NewStorage(FileResource file, IPublicObjectStorage publicStorage) =>
        NewStorage([file], publicStorage);

    private TenantLogoStorage NewStorage(FileResource[] resources, IPublicObjectStorage publicStorage) =>
        new(
            new InMemoryFileResourceRepository(resources),
            new FilePublication(publicStorage, new FixedClock(Now)),
            publicStorage,
            _auditPublisher,
            _storageUnitOfWork,
            new AllowAllExecutionContext(TenantId),
            new FixedClock(Now),
            NullLoggerFactory.Instance.CreateLogger<TenantLogoStorage>());

    private sealed class UnconfiguredPublicObjectStorage : IPublicObjectStorage
    {
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
}
