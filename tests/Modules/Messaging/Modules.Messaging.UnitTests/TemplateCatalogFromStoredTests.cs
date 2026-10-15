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
}
