using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Las opciones del select «Atributo del documento» (<see cref="ShopifyDocumentSources.Options"/>): la ruta de la dirección
/// siempre primero, y después los nombres de atributo vistos en los pedidos, limpios, sin repetir y en orden fijo. Cada opción
/// tiene que ser una clave que <see cref="ShopifyDocumentSources.Read"/> resuelva, o el select ofrecería algo que no lee nada.
/// </summary>
public sealed class ShopifyDocumentSourcesTests
{
    [Fact]
    public void WithoutAttributesOnlyTheAddressPathIsOffered() =>
        Assert.Equal([ShopifyDocumentSources.ShippingAddressCompany], ShopifyDocumentSources.Options([]));

    [Fact]
    public void TheAddressPathGoesFirstAndTheAttributesAreSorted() =>
        Assert.Equal(
            [ShopifyDocumentSources.ShippingAddressCompany, "cedula", "Documento", "tipo_documento"],
            ShopifyDocumentSources.Options(["tipo_documento", "Documento", "cedula"]));

    [Fact]
    public void BlankKeysAreSkippedAndKeysAreTrimmed() =>
        Assert.Equal(
            [ShopifyDocumentSources.ShippingAddressCompany, "Documento"],
            ShopifyDocumentSources.Options(["", "   ", "  Documento  "]));

    // Read compara el nombre del atributo recortado y sin distinguir mayúsculas: «Cédula» y «cédula» resuelven igual, así que
    // ofrecer las dos sería ofrecer dos veces la misma fuente. Gana la primera en orden ordinal, para que la lista no dependa del
    // orden en que llegaron los pedidos.
    [Fact]
    public void KeysThatResolveTheSameAreOfferedOnce() =>
        Assert.Equal(
            [ShopifyDocumentSources.ShippingAddressCompany, "Cédula"],
            ShopifyDocumentSources.Options(["cédula", " Cédula", "Cédula "]));

    // Un atributo llamado como la ruta de la dirección nunca se lee como atributo: Read la toma por la dirección.
    [Fact]
    public void AnAttributeNamedLikeTheAddressPathIsNotRepeated() =>
        Assert.Equal(
            [ShopifyDocumentSources.ShippingAddressCompany, "Documento"],
            ShopifyDocumentSources.Options([" shipping_address.company ", "Documento"]));

    // El PUT de la tienda rechaza una clave más larga que AttributeKeyMaxLength: ofrecerla dejaría elegir algo que no se guarda.
    [Fact]
    public void KeysTooLongToSaveAreNotOffered()
    {
        var longest = new string('a', ShopifyStore.AttributeKeyMaxLength);
        var tooLong = new string('b', ShopifyStore.AttributeKeyMaxLength + 1);

        Assert.Equal(
            [ShopifyDocumentSources.ShippingAddressCompany, longest],
            ShopifyDocumentSources.Options([tooLong, longest]));
    }

    [Fact]
    public void EveryOfferedKeyResolvesAgainstTheOrderItCameFrom()
    {
        var order = ImporterWorld.Order() with
        {
            ShippingAddress = new ShopifyAddress("Ana", "Calle 1", null, "Medellín", "Antioquia", "ANT", "CO", null, "900123456"),
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [" Cédula "] = "1036000111",
                ["tipo_documento"] = "CC",
            },
        };

        var resolved = ShopifyDocumentSources.Options(order.Attributes.Keys)
            .Select(key => ShopifyDocumentSources.Read(order, key) ?? "<nada>")
            .ToArray();

        Assert.Equal(["900123456", "1036000111", "CC"], resolved);
    }
}
