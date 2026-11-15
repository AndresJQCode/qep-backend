using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>Los límites de los adjuntos del formulario público (plan F34; owner, 2026-11-15): tres archivos, 10 MB cada uno, PDF,
/// JPEG, PNG y WebP, y un nombre de hasta 260 sin ruta. Que Storage acepte cada tipo lo amarra <c>SupportAttachmentParityTests</c>.</summary>
public sealed class SupportPublicAttachmentRulesTests
{
    [Fact]
    public void TheLimitsAreThreeFilesOfTenMegabytes()
    {
        Assert.Equal(3, SupportPublicAttachmentRules.MaxFiles);
        Assert.Equal(10L * 1024 * 1024, SupportPublicAttachmentRules.MaxSizeBytes);
        Assert.Equal(260, SupportPublicAttachmentRules.NameMaxLength);
    }

    // Owner, 2026-11-15 (E2): el limitador por IP no frena a muchas IPs a la vez; este tope por tenant sí.
    [Fact]
    public void ATenantHasAtMostTwoHundredOpenUploadsPerDay()
    {
        Assert.Equal(200, SupportPublicAttachmentRules.MaxPendingPerTenantPerDay);
        Assert.Equal(TimeSpan.FromHours(24), SupportPublicAttachmentRules.PendingWindow);
        Assert.Equal("support.public.attachments_busy", SupportErrorCodes.PublicAttachmentsBusy);
    }

    [Theory]
    [InlineData("acta.pdf", "application/pdf", true)]
    [InlineData("FOTO.JPG", "image/jpeg", true)]
    [InlineData("foto.jpeg", "IMAGE/JPEG", true)]
    [InlineData("foto.jpg", "image/jpg", true)]
    [InlineData("foto.png", "image/png", true)]
    [InlineData("foto.webp", "image/webp", true)]
    [InlineData("carta.doc", "application/msword", false)]
    [InlineData("carta.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", false)]
    [InlineData("hoja.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", false)]
    [InlineData("hoja.xls", "application/vnd.ms-excel", false)]
    [InlineData("foto.png", "image/jpg", false)]
    [InlineData("foto.png", "image/jpeg", false)]
    [InlineData("script.exe", "application/octet-stream", false)]
    [InlineData("sin-extension", "application/pdf", false)]
    [InlineData(null, "application/pdf", false)]
    [InlineData("acta.pdf", null, false)]
    public void OnlyTheFormTypesWithTheirMimeArePassed(string? name, string? mimeType, bool allowed) =>
        Assert.Equal(allowed, SupportPublicAttachmentRules.IsAllowedType(name, mimeType));

    // Owner, 2026-11-15: la página lista estos cuatro en su accept, y los recibe del backend (SupportPublicAttachmentsDto). Sin Word
    // ni Excel mientras ClamAV esté apagado en producción.
    [Fact]
    public void TheFormAcceptsExactlyFourMediaTypes() =>
        Assert.Equal(
            [
                "application/pdf",
                "image/jpeg",
                "image/png",
                "image/webp",
            ],
            SupportPublicAttachmentRules.MimeTypes.Order(StringComparer.Ordinal));

    // image/jpg no es un tipo registrado, pero algunos navegadores y el accept de la página lo usan: se toma como image/jpeg, que es lo
    // que Storage acepta y firma.
    [Theory]
    [InlineData("image/jpg", "image/jpeg")]
    [InlineData(" IMAGE/JPG ", "image/jpeg")]
    [InlineData("image/jpeg", "image/jpeg")]
    [InlineData(" application/pdf ", "application/pdf")]
    public void ImageJpgIsAnAliasOfImageJpeg(string mimeType, string normalized) =>
        Assert.Equal(normalized, SupportPublicAttachmentRules.NormalizeMimeType(mimeType));

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
    public void TheFilingValidatorRejectsFourAttachments()
    {
        var four = Enumerable.Range(0, 4).Select(_ => new SupportPublicAttachmentRef(Guid.CreateVersion7(), Guid.NewGuid())).ToList();
        var repeated = new SupportPublicAttachmentRef(Guid.CreateVersion7(), Guid.NewGuid());

        Assert.False(FilePublicTicketValidator.AttachmentsFit(four));
        Assert.True(FilePublicTicketValidator.AttachmentsFit(four.Take(3).ToList()));
        Assert.True(FilePublicTicketValidator.AttachmentsFit(null));
        Assert.False(FilePublicTicketValidator.AttachmentsFit([repeated, repeated]));
        Assert.False(FilePublicTicketValidator.AttachmentsFit([new SupportPublicAttachmentRef(Guid.Empty, Guid.NewGuid())]));
        Assert.False(FilePublicTicketValidator.AttachmentsFit([new SupportPublicAttachmentRef(Guid.CreateVersion7(), null)]));
    }
}
