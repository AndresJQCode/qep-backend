using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-17 (imagen fija del encabezado): la sesión de subida de la imagen. La autoriza sólo
/// <c>messaging.template.manage</c>, y el formato y el tope se deciden antes de tocar Storage.</summary>
public sealed class TemplateHeaderImageSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public MessagingTestBed Bed { get; } = new();

        public RecordingTemplateImageStore Store { get; } = new();

        public Task<TemplateHeaderImageSessionDto> OpenAsync(
            string? fileName = "banner.png", string? mimeType = "image/png", long sizeBytes = 1024, params string[] permissions) =>
            new CreateTemplateHeaderImageSessionHandler(
                    Store,
                    Bed.Connections,
                    Bed.TenantModules,
                    new FakeExecutionContext(
                        Bed.TenantId, Bed.SubjectId, permissions.Length == 0 ? [MessagingPermissions.TemplateManage] : permissions),
                    new CreateTemplateHeaderImageSessionValidator())
                .HandleAsync(new CreateTemplateHeaderImageSessionCommand(Bed.TenantId, Bed.ConnectionId, fileName, mimeType, sizeBytes), Ct);
    }

    [Fact]
    public async Task ASessionForAPngUnderFiveMegabytesOpensAnUpload()
    {
        var rig = new Rig();

        var session = await rig.OpenAsync();

        Assert.Equal(rig.Store.FileId, session.FileId);
        Assert.Equal(rig.Store.UploadUrl.AbsoluteUri, session.UploadUrl);
        Assert.Equal(rig.Store.Headers, session.Headers);
        Assert.Equal([(rig.Bed.TenantId, "banner.png", "image/png", 1024L)], rig.Store.Opened);
    }

    [Fact]
    public async Task AGifIsUnsupportedBeforeAnyUpload()
    {
        var rig = new Rig();

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.OpenAsync(fileName: "banner.gif", mimeType: "image/gif"));

        Assert.Equal(MessagingErrorCodes.TemplateHeaderImageUnsupported, error.Code);
        Assert.Empty(error.FieldErrors);
        Assert.Empty(rig.Store.Opened);
    }

    [Fact]
    public async Task MoreThanFiveMegabytesIsTooLarge()
    {
        var rig = new Rig();

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.OpenAsync(sizeBytes: 5 * 1024 * 1024 + 1));

        Assert.Equal(MessagingErrorCodes.TemplateHeaderImageTooLarge, error.Code);
        Assert.Empty(error.FieldErrors);
        Assert.Empty(rig.Store.Opened);
    }

    [Fact]
    public async Task ExactlyFiveMegabytesOfJpegIsAccepted()
    {
        var rig = new Rig();

        await rig.OpenAsync(fileName: "banner.jpg", mimeType: " IMAGE/JPEG ", sizeBytes: TemplateHeaderImagePolicy.MaxSizeBytes);

        Assert.Single(rig.Store.Opened);
    }

    // Como todo handler de administración de plantillas: una conexión ajena, pausada o borrada es 422 connection_unavailable, antes de
    // tocar Storage.
    [Fact]
    public async Task AForeignConnectionIsUnavailableBeforeAnyUpload()
    {
        var rig = new Rig();
        rig.Bed.Connections.TemplateSource = null;

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.OpenAsync());

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, error.Code);
        Assert.Empty(rig.Store.Opened);
    }

    // El orden es autorización, validador, conexión y después formato: un cuerpo inválido no pregunta por la conexión.
    [Fact]
    public async Task AnInvalidBodyFailsValidationBeforeTheConnection()
    {
        var rig = new Rig();
        rig.Bed.Connections.TemplateSource = null;

        await Assert.ThrowsAsync<ValidationException>(() => rig.OpenAsync(fileName: ""));
    }

    [Fact]
    public async Task AnEmptyFileNameFailsValidation()
    {
        var rig = new Rig();

        var error = await Assert.ThrowsAsync<ValidationException>(() => rig.OpenAsync(fileName: " "));

        Assert.Equal("fileName", Assert.Single(error.Errors).PropertyName);
        Assert.Empty(rig.Store.Opened);
    }

    [Fact]
    public async Task WithoutTemplateManageItIsForbidden()
    {
        var rig = new Rig();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(
            () => rig.OpenAsync(permissions: [MessagingPermissions.ConversationRead]));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Empty(rig.Store.Opened);
    }

    [Fact]
    public void AFieldErrorTravelsInTheException()
    {
        var error = new MessagingDomainException("c", "m", fieldErrors: new Dictionary<string, string[]> { ["x"] = ["y"] });

        Assert.Equal(["y"], error.FieldErrors["x"]);
    }

    [Fact]
    public void WithoutFieldErrorsTheMapIsEmpty()
    {
        Assert.Empty(new MessagingDomainException("c", "m").FieldErrors);
    }
}

/// <summary>Registra las sesiones que se abren; el resto del puerto no se usa en estas pruebas.</summary>
internal sealed class RecordingTemplateImageStore : IMessagingTemplateImageStore
{
    public Guid FileId { get; } = Guid.CreateVersion7();

    public Uri UploadUrl { get; } = new("https://r2.test/staging/banner?signature=abc");

    public IReadOnlyDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = "image/png", ["If-None-Match"] = "*" };

    public List<(Guid TenantId, string FileName, string MimeType, long SizeBytes)> Opened { get; } = [];

    public Task<TemplateImageUploadSession> OpenUploadAsync(
        Guid tenantId, string fileName, string mimeType, long sizeBytes, CancellationToken cancellationToken)
    {
        Opened.Add((tenantId, fileName, mimeType, sizeBytes));
        return Task.FromResult(new TemplateImageUploadSession(FileId, UploadUrl, Headers));
    }

    public Task<TemplateImageFile> CompleteAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<byte[]> ReadAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<string> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ReleaseAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) => throw new NotSupportedException();
}
