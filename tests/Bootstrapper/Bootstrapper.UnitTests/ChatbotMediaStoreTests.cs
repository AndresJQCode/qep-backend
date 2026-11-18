using Modules.Automations.Application;
using Modules.Automations.Domain;
using Modules.Messaging.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-18: el archivo de un bloque «Mensaje» sobre Storage. Es de dueño de sistema <see cref="FileOwnerType.ChatbotMedia"/> con
/// el tenant como dueño, sube por <see cref="IStorageSystemUploads"/> y se publica al completar sin pasar por los handlers ni los permisos
/// de Storage (lo autoriza <c>automations.chatbot.manage</c>). Resolver siempre filtra por tenant: un id ajeno no existe.
/// </summary>
public sealed class ChatbotMediaStoreTests
{
    private const long SixteenMegabytes = 16L * 1024 * 1024;
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeStorageSystemUploads _uploads = new();
    private readonly RecordingPublicObjectStorage _publicStorage = new();
    private readonly RecordingStorageAuditPublisher _audit = new();
    private readonly CountingStorageUnitOfWork _unitOfWork = new();
    private readonly AllowAllExecutionContext _executionContext = new(TenantId);

    [Fact]
    public async Task OpenCreatesAChatbotMediaFileOwnedByTheTenantWithTheRuleCap()
    {
        var session = await NewStore().OpenUploadAsync(TenantId, "demo.mp4", "video/mp4", 1024, SixteenMegabytes, Ct);

        var opened = Assert.Single(_uploads.Opens);
        Assert.Equal((TenantId, FileOwnerType.ChatbotMedia, TenantId), (opened.TenantId, opened.OwnerType, opened.OwnerId));
        Assert.Equal(("demo.mp4", "video/mp4", 1024L, SixteenMegabytes), (opened.Name, opened.MimeType, opened.SizeBytes, opened.MaxSizeBytes));
        Assert.Equal(_uploads.Session.FileId, session.FileId);
        Assert.Equal(_uploads.Session.UploadUrl, session.UploadUrl);
        Assert.Equal(_uploads.Session.Headers, session.Headers);
    }

    // La política del chatbot ya revisó tipo y tope; si Storage igual rechaza la declaración, el editor ve el código de Automations.
    [Theory]
    [InlineData("demo.gif", "image/gif", 1024, AutomationsErrorCodes.MediaUnsupportedType, "fileName")]
    [InlineData("demo.mp4", "audio/mpeg", 1024, AutomationsErrorCodes.MediaUnsupportedType, "fileName")]
    [InlineData("demo.mp4", "video/mp4", SixteenMegabytes + 1, AutomationsErrorCodes.MediaTooLarge, "sizeBytes")]
    public async Task AStorageDeclarationRejectionIsTranslated(string fileName, string mimeType, long sizeBytes, string code, string field)
    {
        var error = await Assert.ThrowsAsync<AutomationsDomainException>(
            () => NewStore().OpenUploadAsync(TenantId, fileName, mimeType, sizeBytes, SixteenMegabytes, Ct));

        Assert.Equal(code, error.Code);
        Assert.True(error.FieldErrors.ContainsKey(field));
        Assert.Empty(_uploads.Opens);
    }

    [Fact]
    public async Task CompletePublishesOnceAndReturnsThePublicUrl()
    {
        var file = AvailableFile(TenantId, TenantId, FileOwnerType.ChatbotMedia, "lista.pdf", "application/pdf");
        _uploads.Seed(TenantId, FileOwnerType.ChatbotMedia, TenantId, SystemUploadOutcome.Available, fileId: file.Id.Value);
        var store = NewStore(file);

        var first = await store.CompleteAsync(TenantId, file.Id.Value, Ct);
        var again = await store.CompleteAsync(TenantId, file.Id.Value, Ct);

        Assert.Equal(ChatbotMediaState.Available, first.State);
        Assert.NotNull(file.PublicStorageKey);
        var expected = new ChatbotMediaFile(
            file.Id.Value, "document", "lista.pdf", "application/pdf", 2048, new Uri(_publicStorage.GetUrl(file.PublicStorageKey)));
        Assert.Equal(expected, first.File);
        Assert.Equal(expected, again.File);
        // Una sola copia pública, una sola auditoría a nombre de quien llama y un solo guardado: el segundo completar no publica.
        Assert.Single(_publicStorage.Copies);
        Assert.Equal([("storage.file.published", _executionContext.SubjectId)], _audit.ByActor);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Theory]
    [InlineData(SystemUploadOutcome.ObjectMissing, ChatbotMediaState.NotUploaded)]
    [InlineData(SystemUploadOutcome.Rejected, ChatbotMediaState.Rejected)]
    [InlineData(SystemUploadOutcome.NotFound, ChatbotMediaState.NotFound)]
    public async Task CompleteMapsObjectMissingToNotUploadedAndRejectedToRejected(SystemUploadOutcome outcome, ChatbotMediaState expected)
    {
        var fileId = _uploads.Seed(TenantId, FileOwnerType.ChatbotMedia, TenantId, outcome);

        var completion = await NewStore().CompleteAsync(TenantId, fileId, Ct);

        Assert.Equal(new ChatbotMediaCompletion(expected, null), completion);
        Assert.Empty(_publicStorage.Copies);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ACompleteOfAnotherTenantsFileIsNotFound()
    {
        var otherTenant = Guid.CreateVersion7();
        var foreign = AvailableFile(otherTenant, otherTenant, FileOwnerType.ChatbotMedia, "catalogo.png", "image/png");
        _uploads.Seed(otherTenant, FileOwnerType.ChatbotMedia, otherTenant, SystemUploadOutcome.Available, fileId: foreign.Id.Value);

        var completion = await NewStore(foreign).CompleteAsync(TenantId, foreign.Id.Value, Ct);

        Assert.Equal(ChatbotMediaState.NotFound, completion.State);
        Assert.Null(foreign.PublicStorageKey);
    }

    [Fact]
    public async Task ResolveIgnoresAnotherTenantsFile()
    {
        var mine = Published(AvailableFile(TenantId, TenantId, FileOwnerType.ChatbotMedia, "catalogo.png", "image/png"));
        var otherTenant = Guid.CreateVersion7();
        var foreign = Published(AvailableFile(otherTenant, otherTenant, FileOwnerType.ChatbotMedia, "catalogo.png", "image/png"));
        var otherOwnerType = Published(AvailableFile(TenantId, TenantId, FileOwnerType.MessageTemplate, "banner.png", "image/png"));
        var deleted = Published(AvailableFile(TenantId, TenantId, FileOwnerType.ChatbotMedia, "viejo.png", "image/png"));
        deleted.SoftDelete(Now);
        var unpublished = AvailableFile(TenantId, TenantId, FileOwnerType.ChatbotMedia, "sin-publicar.png", "image/png");
        var repository = new InMemoryFileResourceRepository(mine, foreign, otherOwnerType, deleted, unpublished);

        var resolved = await NewStore(repository).ResolveAsync(
            TenantId, [mine.Id.Value, foreign.Id.Value, otherOwnerType.Id.Value, deleted.Id.Value, unpublished.Id.Value, Guid.CreateVersion7()], Ct);

        var file = Assert.Single(resolved);
        Assert.Equal(
            new ChatbotMediaFile(mine.Id.Value, "image", "catalogo.png", "image/png", 2048, new Uri(_publicStorage.GetUrl(mine.PublicStorageKey!))),
            file);
        Assert.Equal(1, repository.ListByIdsCalls);
    }

    [Fact]
    public async Task ResolveOfNothingDoesNotQuery()
    {
        var repository = new InMemoryFileResourceRepository();

        Assert.Empty(await NewStore(repository).ResolveAsync(TenantId, [], Ct));
        Assert.Equal(0, repository.ListByIdsCalls);
    }

    private static FileResource AvailableFile(Guid tenantId, Guid ownerId, FileOwnerType ownerType, string name, string mimeType)
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), tenantId, ownerId, ownerType, name, mimeType, 2048, $"staging/tenants/{tenantId:N}/{name}", Now);
        file.CompleteUpload("checksum", 2048, Now);
        file.MarkClean(Now);
        return file;
    }

    private static FileResource Published(FileResource file)
    {
        file.Publish($"tenants/{file.TenantId:N}/media/{file.Id.Value:N}/{file.Name}", Now);
        return file;
    }

    private ChatbotMediaStore NewStore(params FileResource[] resources) => NewStore(new InMemoryFileResourceRepository(resources));

    private ChatbotMediaStore NewStore(InMemoryFileResourceRepository repository) =>
        new(
            _uploads,
            repository,
            new FilePublication(_publicStorage, new FixedClock(Now)),
            _publicStorage,
            _audit,
            _unitOfWork,
            _executionContext,
            new FixedClock(Now));
}

/// <summary>Spec 2026-11-18: Storage revisa y comprime el archivo de un chatbot con el sniffer y el compresor de Mensajería.</summary>
public sealed class ChatbotMediaProcessingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheHeaderHasToMatchTheExtension()
    {
        var processing = new ChatbotMediaProcessing(new FixedCompressor(null));
        var mp3 = "ID3\u0004\0\0\0\0\0\0"u8.ToArray();

        Assert.True(processing.Matches("jingle.mp3", mp3));
        Assert.True(processing.Matches(" JINGLE.MP3 ", mp3));
        Assert.False(processing.Matches("jingle.ogg", mp3));
        Assert.False(processing.Matches("jingle.webp", mp3));
    }

    [Fact]
    public async Task ShrinkReturnsAJpeg()
    {
        var processing = new ChatbotMediaProcessing(new FixedCompressor(new CompressedImage([0xFF, 0xD8, 0xFF, 0x01], 1600, 900)));

        var shrunk = await processing.ShrinkAsync([0x89, 0x50], Ct);

        Assert.NotNull(shrunk);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0x01], shrunk.Content);
        Assert.Equal(("image/jpeg", ".jpg"), (shrunk.MimeType, shrunk.Extension));
    }

    [Fact]
    public async Task AnUnreadableImageShrinksToNull()
    {
        Assert.Null(await new ChatbotMediaProcessing(new FixedCompressor(null)).ShrinkAsync([1, 2, 3], Ct));
    }

    private sealed class FixedCompressor(CompressedImage? result) : IMessagingImageCompressor
    {
        public Task<CompressedImage?> CompressAsync(Stream source, CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
