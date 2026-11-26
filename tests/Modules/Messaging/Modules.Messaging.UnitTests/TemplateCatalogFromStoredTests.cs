using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 D-T8: el catálogo lee del espejo. <c>FromStored</c> normaliza una fila (nombre, idioma, categoría, formato y
/// los <c>components</c> crudos) exactamente como <c>Normalize</c> normaliza un elemento del listado de Meta.</summary>
public sealed class TemplateCatalogFromStoredTests
{
    [Fact]
    public void AStoredRowNormalizesLikeTheMetaListing()
    {
        const string components = """
            [{"type":"HEADER","format":"TEXT","text":"Oferta"},
             {"type":"BODY","text":"Hola {{first_name}}, {{product}} con descuento"},
             {"type":"FOOTER","text":"QEP"},
             {"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Detener"},{"type":"URL","text":"Ver","url":"https://qep.co/x"}]}]
            """;

        var dto = TemplateCatalog.FromStored("promo", "es", "MARKETING", "NAMED", components);

        Assert.Equal(("promo", "es", "MARKETING", TemplateCatalog.Named), (dto.Name, dto.Language, dto.Category, dto.ParameterFormat));
        Assert.Equal(("Oferta", "Hola {{first_name}}, {{product}} con descuento", "QEP"), (dto.Header, dto.Body, dto.Footer));
        Assert.Equal(["first_name", "product"], dto.Parameters);
        Assert.Equal(["QuickReply", "Url"], dto.Buttons.Select(button => button.Type));
        Assert.True(dto.Sendable);
    }

    [Fact]
    public void ADocumentHeaderIsNotSendableFromTheInboxAndSaysWhy()
    {
        var dto = TemplateCatalog.FromStored("qep_cotizacion", "es", "UTILITY", "NAMED",
            """[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{fullname}}"}]""");

        Assert.False(dto.Sendable);
        Assert.Equal(TemplateUnsupportedReasons.MediaHeader, dto.UnsupportedReason);
        Assert.Null(dto.Header);
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("")]
    public void UnreadableComponentsGiveAnEmptyBodyNotAnException(string components)
    {
        var dto = TemplateCatalog.FromStored("x", "es", "UTILITY", "POSITIONAL", components);

        Assert.Equal(string.Empty, dto.Body);
        Assert.Empty(dto.Parameters);
        Assert.Equal(TemplateCatalog.Positional, dto.ParameterFormat);
    }

    [Theory]
    [InlineData("IMAGE")]
    [InlineData("VIDEO")]
    [InlineData("DOCUMENT")]
    [InlineData("LOCATION")]
    public void AStoredMediaHeaderNormalizesLikeTheMetaListing(string format)
    {
        var components = $$"""[{"type":"HEADER","format":"{{format}}"},{"type":"BODY","text":"Hola {{1}}"}]""";

        var stored = TemplateCatalog.FromStored("aviso_logo", "es", "UTILITY", "POSITIONAL", components);
        var listed = Assert.Single(TemplateCatalog.Normalize(
        [
            System.Text.Json.JsonDocument.Parse(
                $$"""{"name":"aviso_logo","language":"es","status":"APPROVED","category":"UTILITY","components":{{components}}}""").RootElement.Clone(),
        ]));

        Assert.Equal(
            (listed.Sendable, listed.UnsupportedReason, listed.HeaderMedia, listed.SendableWithHeaderMedia),
            (stored.Sendable, stored.UnsupportedReason, stored.HeaderMedia, stored.SendableWithHeaderMedia));
    }
    // Spec 2026-11-17: la imagen fija que QEP guardó en la fila hace enviable a una plantilla de encabezado IMAGE.
    private const string StoredUrl = "https://public.test/imagen.png";

    private const string ImageComponents = """[{"type":"HEADER","format":"IMAGE","example":{"header_handle":["https://scontent.whatsapp.net/x"]}},{"type":"BODY","text":"Hola {{first_name}}"}]""";

    [Fact]
    public void AnImageTemplateWithAStoredUrlIsSendable()
    {
        var dto = TemplateCatalog.FromStored("promo_imagen", "es", "MARKETING", "NAMED", ImageComponents, StoredUrl);

        Assert.True(dto.Sendable);
        Assert.Null(dto.UnsupportedReason);
        Assert.Equal(StoredUrl, dto.HeaderImageUrl);
        Assert.Equal(TemplateHeaderMedia.Image, dto.HeaderMedia);
        Assert.Null(dto.Header);
    }

    // Review Focus 4: una de la consola de Meta, sin URL guardada, sigue como antes.
    [Fact]
    public void AnImageTemplateWithoutAStoredUrlStaysMediaHeader()
    {
        var dto = TemplateCatalog.FromStored("promo_imagen", "es", "MARKETING", "NAMED", ImageComponents);

        Assert.False(dto.Sendable);
        Assert.Equal(TemplateUnsupportedReasons.MediaHeader, dto.UnsupportedReason);
        Assert.Null(dto.HeaderImageUrl);
    }

    [Theory]
    [InlineData("""[{"type":"HEADER","format":"VIDEO"},{"type":"BODY","text":"Hola"}]""")]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola"}]""")]
    [InlineData("""[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Hola"},{"type":"BUTTONS","buttons":[{"type":"URL","text":"Ver","url":"https://qep.co/{{1}}/x"}]}]""")]
    [InlineData("""[{"type":"HEADER","format":"TEXT","text":"Oferta"},{"type":"BODY","text":"Hola"},{"type":"CAROUSEL"}]""")]
    public void AStoredUrlDoesNotUnlockAVideoHeaderOrADynamicUrlButton(string components)
    {
        var dto = TemplateCatalog.FromStored("promo", "es", "MARKETING", "NAMED", components, StoredUrl);

        Assert.False(dto.Sendable);
        Assert.NotNull(dto.UnsupportedReason);
        Assert.Null(dto.HeaderImageUrl);
    }

    [Fact]
    public void StoredHeaderOfIsAnImageLink()
    {
        var dto = TemplateCatalog.FromStored("promo_imagen", "es", "MARKETING", "NAMED", ImageComponents, StoredUrl);

        var header = TemplateCatalog.StoredHeaderOf(dto);

        Assert.Equal(TemplateHeader.ByLink("image", StoredUrl, null), header);
        Assert.Null(TemplateCatalog.StoredHeaderOf(TemplateCatalog.FromStored("promo_imagen", "es", "MARKETING", "NAMED", ImageComponents)));
    }

    // Review Focus 5: la API externa sigue exigiendo su propio encabezado aunque haya una imagen guardada.
    [Fact]
    public void TheExternalRuleStillRequiresTheCallersImageHeader()
    {
        var dto = TemplateCatalog.FromStored("promo_imagen", "es", "MARKETING", "NAMED", ImageComponents, StoredUrl);

        var error = Assert.Throws<Modules.Messaging.Domain.MessagingDomainException>(
            () => TemplateCatalog.RequireExternallySendable([dto], "promo_imagen", "es", headerMedia: null, ["first_name"]));

        Assert.Equal(Modules.Messaging.Domain.MessagingErrorCodes.TemplateHeaderMismatch, error.Code);
    }
}
