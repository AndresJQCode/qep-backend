using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-17: el adaptador de la imagen del encabezado de plantilla sobre Storage. El archivo es de dueño de sistema
/// <see cref="FileOwnerType.MessageTemplate"/> con el tenant como dueño; publicar y liberar no pasan por los handlers de Storage ni por
/// sus permisos (los autoriza <c>messaging.template.manage</c>), como <see cref="TenantLogoStorage"/>.
/// </summary>
public sealed class MessagingTemplateImageStoreTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeStorageSystemUploads _uploads = new();
    private readonly RecordingPublicObjectStorage _publicStorage = new();
    private readonly BytesObjectStorage _objectStorage = new();
    private readonly RecordingStorageAuditPublisher _audit = new();
    private readonly CountingStorageUnitOfWork _unitOfWork = new();
    private readonly AllowAllExecutionContext _executionContext = new(TenantId);

    [Fact]
    public async Task OpenCreatesAMessageTemplateFileCappedAtFiveMegabytes()
    {
        var session = await NewStore().OpenUploadAsync(TenantId, "banner.png", "image/png", 1024, Ct);

        var opened = Assert.Single(_uploads.Opens);
        Assert.Equal(FileOwnerType.MessageTemplate, opened.OwnerType);
        Assert.Equal(TenantId, opened.OwnerId);
        Assert.Equal(TenantId, opened.TenantId);
        Assert.Equal(("banner.png", "image/png", 1024L), (opened.Name, opened.MimeType, opened.SizeBytes));
        Assert.Equal(TemplateHeaderImagePolicy.MaxSizeBytes, opened.MaxSizeBytes);
        Assert.Equal(_uploads.Session.FileId, session.FileId);
        Assert.Equal(_uploads.Session.UploadUrl, session.UploadUrl);
        Assert.Equal(_uploads.Session.Headers, session.Headers);
    }

    // La política de Mensajería sólo mira el tipo declarado; la extensión la revisa Storage. Su rechazo llega como el error de la imagen,
    // no como un código de Storage que el formulario de plantillas no conoce.
    [Theory]
    [InlineData("banner.gif", "image/png")]
    [InlineData("banner.pdf", "image/jpeg")]
    [InlineData("banner.png", "image/jpeg")]
    public async Task AStorageTypeRejectionIsAnUnsupportedHeaderImage(string fileName, string mimeType)
    {
        var error = await Assert.ThrowsAsync<MessagingDomainException>(
            () => NewStore().OpenUploadAsync(TenantId, fileName, mimeType, 1024, Ct));

        Assert.Equal(MessagingErrorCodes.TemplateHeaderImageUnsupported, error.Code);
        Assert.Empty(error.FieldErrors);
        Assert.Empty(_uploads.Opens);
    }

    [Fact]
    public async Task AStorageSizeRejectionIsATooLargeHeaderImage()
    {
        var error = await Assert.ThrowsAsync<MessagingDomainException>(
            () => NewStore().OpenUploadAsync(TenantId, "banner.png", "image/png", TemplateHeaderImagePolicy.MaxSizeBytes + 1, Ct));

        Assert.Equal(MessagingErrorCodes.TemplateHeaderImageTooLarge, error.Code);
        Assert.Empty(_uploads.Opens);
    }

    [Theory]
    [InlineData(SystemUploadOutcome.Available, TemplateImageState.Available)]
    [InlineData(SystemUploadOutcome.NotFound, TemplateImageState.NotFound)]
    [InlineData(SystemUploadOutcome.ObjectMissing, TemplateImageState.NotUploaded)]
    [InlineData(SystemUploadOutcome.Rejected, TemplateImageState.Rejected)]
    public async Task CompleteMapsEachOutcome(SystemUploadOutcome outcome, TemplateImageState expected)
    {
        var fileId = _uploads.Seed(TenantId, FileOwnerType.MessageTemplate, TenantId, outcome, "image/jpeg", 4096);

        var file = await NewStore().CompleteAsync(TenantId, fileId, Ct);

        Assert.Equal(expected, file.State);
        if (expected is TemplateImageState.Available)
        {
            Assert.Equal(("image/jpeg", 4096L), (file.MimeType, file.SizeBytes));
        }
        else
        {
            Assert.Null(file.MimeType);
        }
    }

    [Fact]
    public async Task AFileOfAnotherTenantIsNotFound()
    {
        var otherTenant = Guid.CreateVersion7();
        var foreign = _uploads.Seed(otherTenant, FileOwnerType.MessageTemplate, otherTenant, SystemUploadOutcome.Available);
        // Del mismo tenant pero de otro dueño (un adjunto de Soporte): tampoco es una imagen de plantilla.
        var otherOwner = _uploads.Seed(TenantId, FileOwnerType.SupportTicket, Guid.CreateVersion7(), SystemUploadOutcome.Available);

        Assert.Equal(TemplateImageState.NotFound, (await NewStore().CompleteAsync(TenantId, foreign, Ct)).State);
        Assert.Equal(TemplateImageState.NotFound, (await NewStore().CompleteAsync(TenantId, otherOwner, Ct)).State);
    }

    [Fact]
    public async Task ReadReturnsTheBytesOfTheStoredObject()
    {
        var file = AvailableImage(TenantId, TenantId, FileOwnerType.MessageTemplate);
        _objectStorage.Objects[file.StorageKey] = [1, 2, 3];

        var bytes = await NewStore(file).ReadAsync(TenantId, file.Id.Value, Ct);

        Assert.Equal([1, 2, 3], bytes);
    }

    [Fact]
    public async Task ReadAndPublishRejectAFileOfAnotherTenant()
    {
        var otherTenant = Guid.CreateVersion7();
        var file = AvailableImage(otherTenant, otherTenant, FileOwnerType.MessageTemplate);
        var store = NewStore(file);

        var read = await Assert.ThrowsAsync<MessagingDomainException>(() => store.ReadAsync(TenantId, file.Id.Value, Ct));
        var publish = await Assert.ThrowsAsync<MessagingDomainException>(() => store.PublishAsync(TenantId, file.Id.Value, Ct));

        Assert.Equal(MessagingErrorCodes.TemplateHeaderImageNotFound, read.Code);
        Assert.Equal(MessagingErrorCodes.TemplateHeaderImageNotFound, publish.Code);
        Assert.Empty(_publicStorage.Copies);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task PublishReturnsThePublicUrlAndAuditsAsTheCaller()
    {
        var file = AvailableImage(TenantId, TenantId, FileOwnerType.MessageTemplate);

        var url = await NewStore(file).PublishAsync(TenantId, file.Id.Value, Ct);

        Assert.NotNull(file.PublicStorageKey);
        Assert.Equal(_publicStorage.GetUrl(file.PublicStorageKey), url);
        Assert.Equal(file.StorageKey, _publicStorage.Copies[file.PublicStorageKey]);
        Assert.Equal([("storage.file.published", _executionContext.SubjectId)], _audit.ByActor);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ReleaseUnpublishesAndSoftDeletes()
    {
        var file = AvailableImage(TenantId, TenantId, FileOwnerType.MessageTemplate);
        var store = NewStore(file);
        await store.PublishAsync(TenantId, file.Id.Value, Ct);
        var publicKey = file.PublicStorageKey!;

        await store.ReleaseAsync(TenantId, file.Id.Value, Ct);

        Assert.Equal(FileResourceStatus.Deleted, file.Status);
        Assert.Null(file.PublicStorageKey);
        Assert.Contains(publicKey, _publicStorage.DeletedKeys);
        Assert.Equal(
            [("storage.file.published", _executionContext.SubjectId), ("storage.file.deleted", _executionContext.SubjectId)], _audit.ByActor);
        Assert.Equal(2, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ReleaseIsANoOpForADeletedOrForeignFile()
    {
        var deleted = AvailableImage(TenantId, TenantId, FileOwnerType.MessageTemplate);
        deleted.SoftDelete(Now);
        var otherTenant = Guid.CreateVersion7();
        var foreign = AvailableImage(otherTenant, otherTenant, FileOwnerType.MessageTemplate);
        var otherOwnerType = AvailableImage(TenantId, TenantId, FileOwnerType.Tenant);
        var store = NewStore(deleted, foreign, otherOwnerType);

        await store.ReleaseAsync(TenantId, deleted.Id.Value, Ct);
        await store.ReleaseAsync(TenantId, foreign.Id.Value, Ct);
        await store.ReleaseAsync(TenantId, otherOwnerType.Id.Value, Ct);
        await store.ReleaseAsync(TenantId, Guid.CreateVersion7(), Ct);

        Assert.Equal(FileResourceStatus.Available, foreign.Status);
        Assert.Equal(FileResourceStatus.Available, otherOwnerType.Status);
        Assert.Empty(_publicStorage.DeletedKeys);
        Assert.Empty(_audit.Actions);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    private static FileResource AvailableImage(Guid tenantId, Guid ownerId, FileOwnerType ownerType)
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), tenantId, ownerId, ownerType,
            "banner.png", "image/png", 2048, $"staging/tenants/{tenantId:N}/banner", Now);
        file.CompleteUpload("checksum", 2048, Now);
        file.MarkClean(Now);
        return file;
    }

    private MessagingTemplateImageStore NewStore(params FileResource[] resources) =>
        new(
            _uploads,
            new InMemoryFileResourceRepository(resources),
            _objectStorage,
            new FilePublication(_publicStorage, new FixedClock(Now)),
            _publicStorage,
            _audit,
            _unitOfWork,
            _executionContext,
            new FixedClock(Now));
}
