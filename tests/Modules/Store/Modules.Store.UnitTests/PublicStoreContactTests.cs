using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Modules.Store.UnitTests;

public sealed class PublicStoreContactTests
{
    private static StoreContact Contact(string? whatsApp) =>
        new(whatsApp, "Calle 1", "L-V 8-6", "https://instagram.com/x", null, null);

    [Theory]
    [InlineData("+57 300 123 4567", "https://wa.me/573001234567")]
    [InlineData("+573001234567", "https://wa.me/573001234567")]
    [InlineData("+57 (300) 123-4567", "https://wa.me/573001234567")]
    public void TheWhatsAppLinkIsWaMeWithTheDigitsOnly(string whatsApp, string expected)
    {
        var contact = PublicStoreContactDto.From(Contact(whatsApp));

        Assert.Equal(whatsApp, contact.WhatsApp);
        Assert.Equal(expected, contact.WhatsAppUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sin número")]
    public void WithoutANumberThereIsNoLink(string? whatsApp)
    {
        Assert.Null(PublicStoreContactDto.From(Contact(whatsApp)).WhatsAppUrl);
    }

    [Fact]
    public void TheLinkTravelsAsNullAndIsNeverOmitted()
    {
        // El storefront lo lee con zod .nullable(): una clave ausente tumba la tienda entera con un 503.
        var json = JsonSerializer.Serialize(PublicStoreContactDto.From(Contact(null)), JsonSerializerOptions.Web);
        var contact = JsonNode.Parse(json)!.AsObject();

        Assert.True(contact.ContainsKey("whatsAppUrl"));
        Assert.Null(contact["whatsAppUrl"]);
        Assert.Equal("Calle 1", (string)contact["address"]!);
    }
}
