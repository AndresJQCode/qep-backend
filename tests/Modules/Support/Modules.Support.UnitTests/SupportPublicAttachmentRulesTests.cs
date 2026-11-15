using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>Los límites de los adjuntos del formulario público (plan F34): cinco archivos, 10 MB cada uno, los tipos de Storage y un
/// nombre de hasta 260 sin ruta. La paridad de los tipos con <c>FileUploadPolicy</c> la amarra <c>SupportAttachmentParityTests</c>.</summary>
public sealed class SupportPublicAttachmentRulesTests
{
    [Fact]
    public void TheLimitsAreFiveFilesOfTenMegabytes()
    {
        Assert.Equal(5, SupportPublicAttachmentRules.MaxFiles);
        Assert.Equal(10L * 1024 * 1024, SupportPublicAttachmentRules.MaxSizeBytes);
        Assert.Equal(260, SupportPublicAttachmentRules.NameMaxLength);
    }

    [Theory]
    [InlineData("acta.pdf", "application/pdf", true)]
    [InlineData("FOTO.JPG", "image/jpeg", true)]
    [InlineData("foto.jpeg", "IMAGE/JPEG", true)]
    [InlineData("hoja.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", true)]
    [InlineData("foto.png", "image/jpeg", false)]
    [InlineData("script.exe", "application/octet-stream", false)]
    [InlineData("sin-extension", "application/pdf", false)]
    [InlineData(null, "application/pdf", false)]
    [InlineData("acta.pdf", null, false)]
    public void OnlyTheStorageTypesWithTheirMimeArePassed(string? name, string? mimeType, bool allowed) =>
        Assert.Equal(allowed, SupportPublicAttachmentRules.IsAllowedType(name, mimeType));

    [Theory]
    [InlineData("acta.pdf", true)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    [InlineData("../../etc/acta.pdf", false)]
    [InlineData("carpeta\\acta.pdf", false)]
    public void TheNameIsAPlainFileName(string? name, bool valid) =>
        Assert.Equal(valid, SupportPublicAttachmentRules.IsValidName(name));

    [Fact]
    public void TheNameFitsIn260()
    {
        Assert.True(SupportPublicAttachmentRules.IsValidName(new string('a', 256) + ".pdf"));
        Assert.False(SupportPublicAttachmentRules.IsValidName(new string('a', 257) + ".pdf"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10 * 1024 * 1024, true)]
    [InlineData((10 * 1024 * 1024) + 1, false)]
    public void TheSizeIsBetweenOneByteAndTenMegabytes(long sizeBytes, bool valid) =>
        Assert.Equal(valid, SupportPublicAttachmentRules.IsValidSize(sizeBytes));

    [Fact]
    public void TheOpenValidatorMarksEachField()
    {
        var validator = new OpenPublicAttachmentValidator();

        var errors = validator.Validate(new OpenPublicAttachmentCommand("mi-empresa", "virus.exe", "application/pdf", 0))
            .Errors.Select(error => error.PropertyName).Distinct().Order().ToList();

        Assert.Equal(["Name", "SizeBytes"], errors);
        Assert.True(validator.Validate(new OpenPublicAttachmentCommand("mi-empresa", "acta.pdf", "application/pdf", 10)).IsValid);
    }

    [Fact]
    public void TheFilingValidatorRejectsSixAttachments()
    {
        var six = Enumerable.Range(0, 6).Select(_ => new SupportPublicAttachmentRef(Guid.CreateVersion7(), Guid.NewGuid())).ToList();
        var repeated = new SupportPublicAttachmentRef(Guid.CreateVersion7(), Guid.NewGuid());

        Assert.False(FilePublicTicketValidator.AttachmentsFit(six));
        Assert.True(FilePublicTicketValidator.AttachmentsFit(six.Take(5).ToList()));
        Assert.True(FilePublicTicketValidator.AttachmentsFit(null));
        Assert.False(FilePublicTicketValidator.AttachmentsFit([repeated, repeated]));
        Assert.False(FilePublicTicketValidator.AttachmentsFit([new SupportPublicAttachmentRef(Guid.Empty, Guid.NewGuid())]));
        Assert.False(FilePublicTicketValidator.AttachmentsFit([new SupportPublicAttachmentRef(Guid.CreateVersion7(), null)]));
    }
}
