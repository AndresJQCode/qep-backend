using System.Text.Json;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-13 §4.2: la regla de «enviable» de la API externa. Una plantilla con encabezado de imagen, video o documento se
/// envía si el integrador manda el encabezado que ella pide; el envío del asesor sigue con <c>RequireSendable</c>.</summary>
public sealed class TemplateCatalogExternalTests
{
    private const string Catalog = """
        [
          { "name": "aviso", "language": "es", "status": "APPROVED", "category": "UTILITY",
            "components": [ { "type": "BODY", "text": "Tu guía {{1}}" } ] },
          { "name": "aviso_logo", "language": "es", "status": "APPROVED", "category": "UTILITY",
            "components": [ { "type": "HEADER", "format": "IMAGE" }, { "type": "BODY", "text": "Tu guía {{1}}" } ] },
          { "name": "promo_flujo", "language": "es", "status": "APPROVED", "category": "MARKETING",
            "components": [ { "type": "HEADER", "format": "IMAGE" }, { "type": "BODY", "text": "Mira {{1}}" },
                            { "type": "BUTTONS", "buttons": [ { "type": "FLOW", "text": "Abrir" } ] } ] },
          { "name": "otp", "language": "es", "status": "APPROVED", "category": "AUTHENTICATION",
            "components": [ { "type": "HEADER", "format": "IMAGE" }, { "type": "BODY", "text": "Código {{1}}" } ] }
        ]
        """;

    private static readonly IReadOnlyList<MessageTemplateDto> Templates =
        TemplateCatalog.Normalize(JsonDocument.Parse(Catalog).RootElement.EnumerateArray().Select(element => element.Clone()).ToArray());

    [Fact]
    public void AnImageTemplateIsExternallySendableWithAnImageHeader() =>
        Assert.Equal("aviso_logo", TemplateCatalog.RequireExternallySendable(Templates, "aviso_logo", "es", TemplateHeaderMedia.Image, ["1"]).Name);

    [Theory]
    [InlineData("aviso_logo", null, "La plantilla requiere un encabezado de imagen.")]
    [InlineData("aviso_logo", "Video", "La plantilla requiere un encabezado de imagen.")]
    [InlineData("aviso", "Image", "La plantilla no tiene encabezado multimedia.")]
    public void AMissingOrWrongHeaderIsHeaderMismatch(string name, string? header, string detail)
    {
        var exception = Assert.Throws<MessagingDomainException>(
            () => TemplateCatalog.RequireExternallySendable(Templates, name, "es", header, ["1"]));

        Assert.Equal(("messaging.template.header_mismatch", detail), (exception.Code, exception.Message));
    }

    [Theory]
    [InlineData("promo_flujo")]
    [InlineData("otp")]
    [InlineData("noexiste")]
    public void ATemplateNotSendableEvenWithTheHeaderIsNotAvailable(string name)
    {
        var exception = Assert.Throws<MessagingDomainException>(
            () => TemplateCatalog.RequireExternallySendable(Templates, name, "es", TemplateHeaderMedia.Image, ["1"]));

        Assert.Equal("messaging.template.not_available", exception.Code);
    }

    [Fact]
    public void TheHeaderIsCheckedBeforeTheParameters()
    {
        var exception = Assert.Throws<MessagingDomainException>(
            () => TemplateCatalog.RequireExternallySendable(Templates, "aviso_logo", "es", null, ["2"]));

        Assert.Equal("messaging.template.header_mismatch", exception.Code);
    }

    [Fact]
    public void TheParametersStillMustMatch()
    {
        var exception = Assert.Throws<MessagingDomainException>(
            () => TemplateCatalog.RequireExternallySendable(Templates, "aviso_logo", "es", TemplateHeaderMedia.Image, ["2"]));

        Assert.Equal("messaging.template.parameters_mismatch", exception.Code);
    }

    [Fact]
    public void TheSharedRuleStillRejectsAnImageTemplate()
    {
        var exception = Assert.Throws<MessagingDomainException>(() => TemplateCatalog.RequireSendable(Templates, "aviso_logo", "es", ["1"]));

        Assert.Equal("messaging.template.not_available", exception.Code);
    }

    [Fact]
    public void OnlyTheExternallySendableAreListed() =>
        Assert.Equal(["aviso", "aviso_logo"], Templates.Where(TemplateCatalog.IsExternallySendable).Select(template => template.Name));
}
