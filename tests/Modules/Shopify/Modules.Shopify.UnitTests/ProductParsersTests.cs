using System.Text.Json;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Admin;
using Modules.Shopify.Infrastructure.Parsing;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-11-17 §5.3 y plan D1: el producto de la Admin API GraphQL y la referencia del webhook REST.</summary>
public sealed class ProductParsersTests
{
    private static JsonElement PageNode(int index)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "products", "products-page.json")));
        return document.RootElement.GetProperty("data").GetProperty("products").GetProperty("nodes")[index].Clone();
    }

    [Fact]
    public void ParsesAProductWithOptionsInOptionOrder()
    {
        var product = GraphQlProductParser.Parse(PageNode(0));

        Assert.Equal((7001L, "  Camiseta básica ", ShopifyProductStatus.Active), (product.Id, product.Title, product.Status));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero), product.UpdatedAt);
        Assert.Equal(["Talla", "Color"], product.Options.Select(option => option.Name));
        Assert.Equal(["S", "M"], product.Options[0].Values);
        // selectedOptions llegan desordenadas: los valores salen en el orden de options.
        Assert.Equal(["S", "Rojo"], product.Variants[0].OptionValues);
        Assert.Equal(["M", "Rojo"], product.Variants[1].OptionValues);
        Assert.Equal((4455L, "CAM-S-R", "7701234567890", 50_000m, true), (
            product.Variants[0].Id, product.Variants[0].Sku, product.Variants[0].Barcode, product.Variants[0].Price, product.Variants[0].Taxable));
        // Sin SKU y con barras vacías: null, no texto vacío.
        Assert.Equal(((string?)null, (string?)null, 52_000.50m, false), (
            product.Variants[1].Sku, product.Variants[1].Barcode, product.Variants[1].Price, product.Variants[1].Taxable));
    }

    [Fact]
    public void ParsesTheFeaturedImageIdFromTheMediaGid()
    {
        var product = GraphQlProductParser.Parse(PageNode(0));

        Assert.Equal(9L, product.FeaturedImage!.Id);
        Assert.Equal("https://cdn.shopify.com/s/files/1/0001/camiseta.jpg?v=1&width=2048", product.FeaturedImage.Url.AbsoluteUri);
    }

    [Fact]
    public void ADraftWithoutImageAndDefaultTitleParses()
    {
        var product = GraphQlProductParser.Parse(PageNode(1));

        Assert.Equal(ShopifyProductStatus.Draft, product.Status);
        Assert.Null(product.FeaturedImage);
        Assert.Equal(["Default Title"], Assert.Single(product.Variants).OptionValues);
        Assert.Equal(30_000m, product.Variants[0].Price);
    }

    [Fact]
    public void AnUnknownStatusIsAPayloadError()
    {
        var node = JsonSerializer.SerializeToNode(PageNode(1))!;
        node["status"] = "SOMETHING_ELSE";

        Assert.Throws<ShopifyPayloadException>(() => GraphQlProductParser.Parse(JsonSerializer.SerializeToElement(node)));
    }

    [Fact]
    public void TheWebhookRefReadsIdAndUpdatedAt()
    {
        var parser = new ShopifyProductPayloadParser();

        var update = parser.ParseRef("""{"id": 7001, "title": "Camiseta", "updated_at": "2026-11-10T10:00:00-05:00", "variants": []}""");
        var delete = parser.ParseRef("""{"id": 7001}""");

        Assert.Equal(new ShopifyProductWebhookRef(7001L, new DateTimeOffset(2026, 11, 10, 15, 0, 0, TimeSpan.Zero)), update);
        Assert.Equal(new ShopifyProductWebhookRef(7001L, null), delete);
    }

    [Fact]
    public void AWebhookBodyThatIsNotJsonIsAPayloadError() =>
        Assert.Throws<ShopifyPayloadException>(() => new ShopifyProductPayloadParser().ParseRef("not json"));

    // Contrato: cada campo que el parser lee aparece en la consulta (sin fragmentos ni alias). Un campo que no se pida llegaría
    // como null sin error.
    [Theory]
    [InlineData("id")]
    [InlineData("title")]
    [InlineData("status")]
    [InlineData("updatedAt")]
    [InlineData("options { name optionValues { name } }")]
    [InlineData("featuredMedia { id preview { image { url(transform: { preferredContentType: JPG, maxWidth: 2048, maxHeight: 2048 }) } } }")]
    [InlineData("variants(first: 50) { pageInfo { hasNextPage endCursor } nodes { id sku barcode price taxable selectedOptions { name value } } }")]
    public void TheProductQueryAsksForEveryFieldTheParserReads(string fragment)
    {
        Assert.Contains(fragment, ShopifyProductsClient.ProductFields, StringComparison.Ordinal);
        Assert.Contains(ShopifyProductsClient.ProductFields, ShopifyProductsClient.ProductsQuery, StringComparison.Ordinal);
        Assert.Contains(ShopifyProductsClient.ProductFields, ShopifyProductsClient.ProductQuery, StringComparison.Ordinal);
    }

    [Fact]
    public void TheQueriesFilterActiveAndDraftOnly()
    {
        Assert.Equal("status:active OR status:draft", ShopifyProductsClient.ProductsFilter);
        Assert.Contains("product_status:active OR product_status:draft", ShopifyProductsClient.VariantKeysQuery, StringComparison.Ordinal);
        Assert.Contains("products(first: 10, after: $after, query: $query, sortKey: ID)", ShopifyProductsClient.ProductsQuery, StringComparison.Ordinal);
    }
}
