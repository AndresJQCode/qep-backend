using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-20: cambiar la imagen fija del encabezado de una plantilla guardada sin pasar por Meta. La anterior se retira
/// (sigue referenciada, no se libera); sólo la imagen nueva se libera si el guardado falla.</summary>
public sealed class ReplaceTemplateHeaderImageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ImageComponents =
        """[{"type":"HEADER","format":"IMAGE","example":{"header_handle":["https://scontent.whatsapp.net/x"]}},{"type":"BODY","text":"Hola {{first_name}}"}]""";

    private const string TextComponents = """[{"type":"HEADER","format":"TEXT","text":"Oferta"},{"type":"BODY","text":"Hola"}]""";

    private const string DocumentComponents =
        """[{"type":"HEADER","format":"DOCUMENT","example":{"header_handle":["https://scontent.whatsapp.net/x"]}},{"type":"BODY","text":"Hola"}]""";

    private sealed class Rig
    {
        public Rig() => Images.DefaultCreatedAt = Bed.Now;

        public MessagingTestBed Bed { get; } = new();

        public FakeMessageTemplateRepository Repository { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public ScriptedTemplateImageStore Images { get; } = new();

        public RecordingTemplateImageReleases Releases { get; } = new();

        public MessageTemplate Seed(string componentsJson, Guid? currentFileId = null)
        {
            var template = MessageTemplate.Pending(
                Bed.TenantId, Bed.ConnectionId, "555", "promo_imagen", "es", "MARKETING", "NAMED", "APPROVED", componentsJson, MessageTemplateKind.Free,
                Bed.MemberId, Bed.Now.AddDays(-1));
            if (currentFileId is { } id)
            {
                template.AttachHeaderImage(id, $"https://public.test/{id}.png", Bed.Now.AddDays(-1));
                Repository.SavedHeaderImages.Add((Bed.TenantId, id));
            }

            Repository.Rows.Add(template);
            return template;
        }

        public Task<ManagedTemplateDto> ReplaceAsync(Guid templateId, Guid? fileId)
        {
            var executionContext = new FakeExecutionContext(Bed.TenantId, Bed.SubjectId, MessagingPermissions.TemplateManage);
            var handler = new ReplaceTemplateHeaderImageHandler(
                Repository, Bed.Connections, new FakeMemberNames(Bed.MemberId), UnitOfWork, Audit, Bed.TenantModules, executionContext,
                new FakeClock(Bed.Now), new ReplaceTemplateHeaderImageCommandValidator(), Images, Releases,
                new RecordingLogger<ReplaceTemplateHeaderImageHandler>());
            return handler.HandleAsync(new ReplaceTemplateHeaderImageCommand(Bed.TenantId, Bed.ConnectionId, templateId, fileId), Ct);
        }
    }

    [Fact]
    public async Task ReplacesTheImageAndRetiresThePreviousWithoutCallingMeta()
    {
        var rig = new Rig();
        var oldId = Guid.CreateVersion7();
        var template = rig.Seed(ImageComponents, oldId);
        var newId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);

        var dto = await rig.ReplaceAsync(template.Id, newId);

        Assert.Equal($"https://public.test/{newId}.png", dto.HeaderImageUrl);
        Assert.Equal(TemplateHeaderMedia.Image, dto.HeaderMedia);
        Assert.Equal((newId, $"https://public.test/{newId}.png"), (template.HeaderImageFileId, template.HeaderImageUrl));
        var retired = Assert.Single(rig.Repository.RetiredHeaderImages);
        Assert.Equal((rig.Bed.TenantId, template.Id, oldId), (retired.TenantId, retired.TemplateId, retired.FileId));
        // El handler ni siquiera recibe el cliente de Meta; esto deja escrito que ni el estado ni los componentes se tocan.
        Assert.Empty(rig.Bed.Meta.CreatedTemplates);
        Assert.Equal("APPROVED", template.Status);
        Assert.Equal(ImageComponents, template.ComponentsJson);
        Assert.Empty(rig.Releases.Published);
        Assert.DoesNotContain("Release", rig.Images.Calls);
        var audit = Assert.Single(rig.Audit.Templates);
        Assert.Equal(template.Id, audit.TemplateId);
        Assert.Equal("messaging.template.header_image_replaced", audit.Action);
        Assert.Equal(1, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AssignsAnImageToASyncedImageTemplate()
    {
        var rig = new Rig();
        var template = rig.Seed(ImageComponents);
        var newId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);

        var dto = await rig.ReplaceAsync(template.Id, newId);

        Assert.Empty(rig.Repository.RetiredHeaderImages);
        Assert.True(dto.Sendable);
        Assert.Equal(TemplateHeaderMedia.Image, dto.HeaderMedia);
        Assert.Equal($"https://public.test/{newId}.png", dto.HeaderImageUrl);
    }

    [Fact]
    public async Task RejectsATextHeaderTemplateWithHeaderMismatch()
    {
        var rig = new Rig();
        var template = rig.Seed(TextComponents);
        var newId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ReplaceAsync(template.Id, newId));

        Assert.Equal(MessagingErrorCodes.TemplateHeaderMismatch, error.Code);
        Assert.Empty(rig.Images.Calls);
        Assert.Null(template.HeaderImageFileId);
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task RejectsADocumentHeaderTemplateWithHeaderMismatch()
    {
        var rig = new Rig();
        var template = rig.Seed(DocumentComponents);
        var newId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ReplaceAsync(template.Id, newId));

        Assert.Equal(MessagingErrorCodes.TemplateHeaderMismatch, error.Code);
        Assert.Empty(rig.Images.Calls);
        Assert.Null(template.HeaderImageFileId);
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }

    // Sin componente HEADER no hay dónde poner la imagen: HeaderMedia es null y se rechaza igual que un encabezado de texto o documento.
    [Fact]
    public async Task RejectsATemplateWithNoHeaderWithHeaderMismatch()
    {
        var rig = new Rig();
        var template = rig.Seed("""[{"type":"BODY","text":"Hola"}]""");
        var newId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ReplaceAsync(template.Id, newId));

        Assert.Equal(MessagingErrorCodes.TemplateHeaderMismatch, error.Code);
        Assert.Empty(rig.Images.Calls);
        Assert.Empty(rig.Releases.Published);
        Assert.Empty(rig.Repository.RetiredHeaderImages);
        Assert.Null(template.HeaderImageFileId);
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task RejectsTheFileTheTemplateAlreadyUses()
    {
        var rig = new Rig();
        var current = Guid.CreateVersion7();
        var template = rig.Seed(ImageComponents, current);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ReplaceAsync(template.Id, current));

        Assert.Equal(MessagingErrorCodes.TemplateHeaderImageRejected, error.Code);
        Assert.Equal([TemplateMessages.HeaderImage], error.FieldErrors["headerImageFileId"]);
        Assert.Empty(rig.Images.Calls);
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }

    // Spec 2026-11-20: una imagen retirada sigue referenciada (Meta puede seguir mostrándola), así que la plantilla no la vuelve a tomar.
    [Fact]
    public async Task RejectsAFileTheTemplateRetiredEarlier()
    {
        var rig = new Rig();
        var template = rig.Seed(ImageComponents, Guid.CreateVersion7());
        var retiredId = Guid.CreateVersion7();
        rig.Repository.RetiredHeaderImages.Add(
            MessageTemplateRetiredHeaderImage.Create(rig.Bed.TenantId, template.Id, retiredId, "https://public.test/r.png", rig.Bed.Now));
        rig.Repository.SavedHeaderImages.Add((rig.Bed.TenantId, retiredId));

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ReplaceAsync(template.Id, retiredId));

        Assert.Equal(MessagingErrorCodes.TemplateHeaderImageRejected, error.Code);
        Assert.Equal([TemplateMessages.HeaderImage], error.FieldErrors["headerImageFileId"]);
        Assert.Empty(rig.Images.Calls);
    }

    [Fact]
    public async Task ReleasesTheNewImageWhenTheSaveFails()
    {
        var rig = new Rig();
        var oldId = Guid.CreateVersion7();
        var template = rig.Seed(ImageComponents, oldId);
        var newId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);
        rig.UnitOfWork.Failure = new InvalidOperationException("db down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.ReplaceAsync(template.Id, newId));

        Assert.Equal(["Complete", "Publish", "Release"], rig.Images.Calls);
        // La anterior nunca se libera: sigue siendo la de la fila guardada.
        Assert.Contains((rig.Bed.TenantId, oldId), rig.Repository.SavedHeaderImages);
        Assert.Empty(rig.Releases.Published);
    }

    [Fact]
    public async Task UnknownTemplateIsNotFound()
    {
        var rig = new Rig();
        var newId = rig.Images.Seed(rig.Bed.TenantId, "image/png", 2048);

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => rig.ReplaceAsync(Guid.CreateVersion7(), newId));

        Assert.Equal(MessagingErrorCodes.TemplateNotFound, error.Code);
        Assert.Empty(rig.Images.Calls);
    }

    [Fact]
    public async Task AMissingFileIdIsAValidationErrorOnTheField()
    {
        var rig = new Rig();
        var template = rig.Seed(ImageComponents);

        var error = await Assert.ThrowsAsync<ValidationException>(() => rig.ReplaceAsync(template.Id, null));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "headerImageFileId" && failure.ErrorMessage == TemplateMessages.HeaderImage);
    }
}
