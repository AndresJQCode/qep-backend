using System.Text.Json;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-17: crear una plantilla libre con la imagen fija del encabezado. El archivo se revisa antes de publicarlo, cada
/// problema sale con su código y la clave <c>headerImageFileId</c>, y todo fallo después de publicar libera la imagen antes de salir.</summary>
public sealed class CreateTemplateWithHeaderImageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly MessagingGraphFailure MetaRejection = new(400, 100, "http_400", "Invalid Format", "The body ends with a variable.");

    private sealed class Rig
    {
        public MessagingTestBed Bed { get; } = new();

        public FakeMessageTemplateRepository Repository { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public ScriptedTemplateImageStore Images { get; } = new();

        public RecordingLogger<CreateTemplateHandler> Logger { get; } = new();

        public RecordingTemplateImageReleases Releases { get; } = new();

        public IMessagingMemberNames? MemberNames { get; set; }

        public Task<ManagedTemplateDto> CreateAsync(Guid? headerImageFileId, string? header = null, CancellationToken? cancellationToken = null)
        {
            var membership = new FakeMembershipDirectory();
            membership.Active[(Bed.SubjectId, Bed.TenantId)] = Bed.MemberId;
            var executionContext = new FakeExecutionContext(Bed.TenantId, Bed.SubjectId, MessagingPermissions.TemplateManage);
            var handler = new CreateTemplateHandler(
                Repository, Bed.Meta, Bed.Connections, MemberNames ?? new FakeMemberNames(Bed.MemberId), new CallerMembership(membership, executionContext), UnitOfWork,
                Audit, Bed.TenantModules, executionContext, new FakeClock(Bed.Now), new CreateTemplateCommandValidator(), Images, Releases, Logger);
            return handler.HandleAsync(
                new CreateTemplateCommand(
                    Bed.TenantId, Bed.ConnectionId, "promo_imagen", "es", "MARKETING", header, "Hola {{first_name}}, mira la oferta.", null, [],
                    new Dictionary<string, string?> { ["first_name"] = "Ana" }, headerImageFileId),
                cancellationToken ?? Ct);
        }
    }

    [Fact]
    public async Task AnAvailablePngIsPublishedUploadedToMetaAsAnImageHeaderAndStoredOnTheRow()
    {
        var rig = new Rig();
        var fileId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);

        var dto = await rig.CreateAsync(fileId);

        Assert.Equal($"https://public.test/{fileId}.png", dto.HeaderImageUrl);
        Assert.Null(dto.Header);
        var upload = Assert.Single(rig.Bed.Meta.ExampleUploads);
        Assert.Equal(("image/png", 2048), (upload.MimeType, upload.Length));
        var header = JsonDocument.Parse(Assert.Single(rig.Bed.Meta.CreatedTemplates).ToJsonString()).RootElement.GetProperty("components")[0];
        Assert.Equal(("HEADER", "IMAGE", "4::handle"), (
            header.GetProperty("type").GetString(), header.GetProperty("format").GetString(),
            header.GetProperty("example").GetProperty("header_handle")[0].GetString()));
        var row = Assert.Single(rig.Repository.Rows);
        Assert.Equal<(Guid?, string?)>((fileId, $"https://public.test/{fileId}.png"), (row.HeaderImageFileId, row.HeaderImageUrl));
        Assert.Contains("\"IMAGE\"", row.ComponentsJson, StringComparison.Ordinal);
        Assert.Equal(["Complete", "Publish", "Read"], rig.Images.Calls);
        Assert.Equal(1, rig.UnitOfWork.Saves);
    }

    [Theory]
    [InlineData(TemplateImageState.NotFound, "image/png", 2048L, MessagingErrorCodes.TemplateHeaderImageNotFound)]
    [InlineData(TemplateImageState.NotUploaded, "image/png", 2048L, MessagingErrorCodes.TemplateHeaderImageNotUploaded)]
    [InlineData(TemplateImageState.Rejected, "image/png", 2048L, MessagingErrorCodes.TemplateHeaderImageRejected)]
    [InlineData(TemplateImageState.Available, "image/gif", 2048L, MessagingErrorCodes.TemplateHeaderImageUnsupported)]
    [InlineData(TemplateImageState.Available, "image/jpeg", 6L * 1024 * 1024, MessagingErrorCodes.TemplateHeaderImageTooLarge)]
    public async Task AFileProblemIsItsCodeOnTheFieldAndNothingIsPublished(TemplateImageState state, string mimeType, long size, string code)
    {
        var rig = new Rig();
        var fileId = rig.Images.Seed(rig.Bed.TenantId, mimeType, size, state);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.CreateAsync(fileId));

        Assert.Equal(code, error.Code);
        Assert.Equal([TemplateMessages.HeaderImage], error.FieldErrors["headerImageFileId"]);
        Assert.Equal(["Complete"], rig.Images.Calls);
        Assert.Empty(rig.Bed.Meta.ExampleUploads);
        Assert.Empty(rig.Bed.Meta.CreatedTemplates);
        Assert.Empty(rig.Repository.Rows);
    }

    [Fact]
    public async Task AnImageOfAnotherTenantIsNotFound()
    {
        var rig = new Rig();
        var fileId = rig.Images.Seed(Guid.CreateVersion7(), "image/png", 2048);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.CreateAsync(fileId));

        Assert.Equal(MessagingErrorCodes.TemplateHeaderImageNotFound, error.Code);
        Assert.Equal(["Complete"], rig.Images.Calls);
    }

    [Fact]
    public async Task AMetaRejectionReleasesThePublishedImage()
    {
        var rig = new Rig();
        var fileId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);
        rig.Bed.Meta.NextCreatedTemplate = new MessagingGraphResult<CreatedTemplate>(null, MetaRejection);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.CreateAsync(fileId));

        Assert.Equal(MessagingErrorCodes.TemplateRejectedByMeta, error.Code);
        Assert.Equal(["Complete", "Publish", "Read", "Release"], rig.Images.Calls);
        Assert.Empty(rig.Repository.Rows);
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AFailedMetaUploadReleasesThePublishedImageAndIsUploadFailed()
    {
        var rig = new Rig();
        var fileId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);
        rig.Bed.Meta.NextHandle = new MessagingGraphResult<string>(null, new MessagingGraphFailure(500, null, "http_500"));

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.CreateAsync(fileId));

        Assert.Equal(MessagingErrorCodes.TemplateUploadFailed, error.Code);
        Assert.Equal(["Complete", "Publish", "Read", "Release"], rig.Images.Calls);
        Assert.Empty(rig.Bed.Meta.CreatedTemplates);
        Assert.Contains(rig.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("header image", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedSaveReleasesThePublishedImage()
    {
        var rig = new Rig();
        var fileId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);
        rig.UnitOfWork.Failure = new MessagingDomainException(MessagingErrorCodes.TemplateNameTaken, "taken");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.CreateAsync(fileId));

        Assert.Equal(MessagingErrorCodes.TemplateNameTaken, error.Code);
        Assert.Equal("Release", rig.Images.Calls[^1]);
    }

    // Plan 2026-11-17 Task 8: si la liberación sincrónica falla (R2 caído), el aviso de liberación va al outbox, guardado solo: se descarta
    // lo que el tracker tenía (la fila que no se pudo guardar) para que no viaje con él. Sale el error original.
    [Fact]
    public async Task AFailedReleaseFallsBackToTheOutboxAndTheOriginalErrorLeaves()
    {
        var rig = new Rig();
        var fileId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);
        rig.Bed.Meta.NextCreatedTemplate = new MessagingGraphResult<CreatedTemplate>(null, MetaRejection);
        rig.Images.ReleaseFailure = new InvalidOperationException("R2 is down");
        var discardsAtPublish = -1;
        rig.Releases.OnPublish = () => discardsAtPublish = rig.UnitOfWork.Discards;

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.CreateAsync(fileId));

        Assert.Equal(MessagingErrorCodes.TemplateRejectedByMeta, error.Code);
        Assert.Equal((rig.Bed.TenantId, fileId, rig.Bed.Now), Assert.Single(rig.Releases.Published));
        Assert.Equal(1, discardsAtPublish);
        Assert.Equal(1, rig.UnitOfWork.Saves);
        Assert.Contains(rig.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains(fileId.ToString(), StringComparison.Ordinal));
    }

    // Una cancelación del request que llega con la liberación caída deja el aviso igual: el guardado del respaldo no usa el token del request.
    [Fact]
    public async Task ACancelledCreateWithAFailedReleaseStillWritesTheRelease()
    {
        var rig = new Rig();
        var fileId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);
        rig.Images.ReleaseFailure = new InvalidOperationException("R2 is down");
        using var request = new CancellationTokenSource();
        rig.Images.OnPublish = request.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.CreateAsync(fileId, cancellationToken: request.Token));

        Assert.Single(rig.Releases.Published);
        Assert.Equal(1, rig.UnitOfWork.Saves);
    }

    // Si también el outbox falla, queda el error en el log con el archivo y sale el error original.
    [Fact]
    public async Task AFailedReleaseAndAFailedOutboxAreLoggedAndTheOriginalErrorLeaves()
    {
        var rig = new Rig();
        var fileId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);
        rig.UnitOfWork.Failure = new MessagingDomainException(MessagingErrorCodes.TemplateNameTaken, "taken");
        rig.Images.ReleaseFailure = new InvalidOperationException("R2 is down");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.CreateAsync(fileId));

        Assert.Equal(MessagingErrorCodes.TemplateNameTaken, error.Code);
        Assert.Contains(rig.Logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains(fileId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASuccessfulReleaseWritesNoOutboxRelease()
    {
        var rig = new Rig();
        var fileId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);
        rig.Bed.Meta.NextCreatedTemplate = new MessagingGraphResult<CreatedTemplate>(null, MetaRejection);

        await Assert.ThrowsAsync<MessagingDomainException>(() => rig.CreateAsync(fileId));

        Assert.Empty(rig.Releases.Published);
        Assert.Equal(0, rig.UnitOfWork.Discards);
    }

    // Revisión del bloque B: un request cancelado después de publicar no deja una imagen pública sin fila.
    [Fact]
    public async Task ACancelledCreateAfterPublishReleasesTheImage()
    {
        var rig = new Rig();
        var fileId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);
        using var request = new CancellationTokenSource();
        rig.Images.OnPublish = request.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.CreateAsync(fileId, cancellationToken: request.Token));

        Assert.Equal(["Complete", "Publish", "Read", "Release"], rig.Images.Calls);
        Assert.Empty(rig.Bed.Meta.CreatedTemplates);
        Assert.Empty(rig.Repository.Rows);
    }

    [Fact]
    public async Task ACancellationAfterTheRowIsSavedDoesNotReleaseTheImage()
    {
        var rig = new Rig();
        var fileId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);
        rig.MemberNames = new CancelledMemberNames();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.CreateAsync(fileId));

        Assert.Equal(1, rig.UnitOfWork.Saves);
        Assert.Equal(["Complete", "Publish", "Read"], rig.Images.Calls);
    }

    /// <summary>Se cancela al leer los nombres para el DTO, que es lo único que corre después de guardar.</summary>
    private sealed class CancelledMemberNames : IMessagingMemberNames
    {
        public Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();

        public Task<string?> FindDisplayNameAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) => throw new OperationCanceledException();
    }

    [Fact]
    public async Task WithoutAnImageNothingTouchesTheStoreAndTheUrlIsNull()
    {
        var rig = new Rig();

        var dto = await rig.CreateAsync(null, header: "Oferta");

        Assert.Null(dto.HeaderImageUrl);
        Assert.Equal("Oferta", dto.Header);
        Assert.Empty(rig.Images.Calls);
        Assert.Empty(rig.Bed.Meta.ExampleUploads);
    }

    [Fact]
    public async Task ATextHeaderAndAnImageTogetherNeverTouchTheFile()
    {
        var rig = new Rig();
        var fileId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => rig.CreateAsync(fileId, header: "Oferta"));

        Assert.Empty(rig.Images.Calls);
    }
}

/// <summary>El puerto de la imagen en memoria, con el orden de las llamadas.</summary>
internal sealed class ScriptedTemplateImageStore : IMessagingTemplateImageStore
{
    private readonly Dictionary<Guid, (Guid TenantId, TemplateImageFile File)> _files = [];

    public List<string> Calls { get; } = [];

    public Exception? ReleaseFailure { get; set; }

    /// <summary>Corre al publicar: sirve para cancelar el request justo después.</summary>
    public Action? OnPublish { get; set; }

    public Guid Seed(Guid tenantId, string mimeType, long size, TemplateImageState state = TemplateImageState.Available)
    {
        var fileId = Guid.CreateVersion7();
        _files[fileId] = (tenantId, new TemplateImageFile(state, mimeType, size));
        return fileId;
    }

    public Task<TemplateImageUploadSession> OpenUploadAsync(
        Guid tenantId, string fileName, string mimeType, long sizeBytes, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<TemplateImageFile> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        Calls.Add("Complete");
        return Task.FromResult(_files.TryGetValue(fileId, out var stored) && stored.TenantId == tenantId
            ? stored.File
            : new TemplateImageFile(TemplateImageState.NotFound, null, 0));
    }

    public Task<byte[]> ReadAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        Calls.Add("Read");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new byte[_files[fileId].File.SizeBytes]);
    }

    public Task<string> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        Calls.Add("Publish");
        OnPublish?.Invoke();
        return Task.FromResult($"https://public.test/{fileId}.png");
    }

    public Task ReleaseAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        Calls.Add("Release");
        return ReleaseFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }
}
