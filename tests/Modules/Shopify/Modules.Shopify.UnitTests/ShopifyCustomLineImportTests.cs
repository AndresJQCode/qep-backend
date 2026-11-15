using System.Text.Json.Nodes;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.ImporterWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-13: una línea personalizada (sin producto ni variante en Shopify) se importa con la variante que la tienda asoció a
/// su nombre exacto; sin asociación (o con una variante inexistente o inactiva) el pedido espera con
/// <c>{"skus":[…],"customTitles":[…]}</c>. Una línea de producto real nunca casa por nombre, y una asociación nunca aplica fuera
/// de su tienda.
/// </summary>
public sealed class ShopifyCustomLineImportTests
{
    private const string Garantia = "Garantía por 1 año + Envío Prioritario🚛";

    private static ShopifyLine Custom(string? title, decimal price = 5_000m) => new(null, 1m, price, 0m, title, true);

    [Fact]
    public async Task ACustomLineWithAMappingImportsWithTheMappedVariant()
    {
        var world = new ImporterWorld();
        var service = Guid.CreateVersion7();
        world.Products.WithVariant(service, taxPercentage: 19);
        world.CustomLines.Map(ConnectionId, Garantia, service);
        var snapshot = Order(lines: [new ShopifyLine("CAM-001", 2m, 119_000m, 0m), Custom(Garantia)]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        Assert.Equal(ShopifyImportState.Imported, outcome.State);
        var (draft, _) = Assert.Single(world.Writer.Creates);
        Assert.Equal(2, draft.Lines.Count);
        var mapped = draft.Lines[1];
        Assert.Equal(service, mapped.VariantId);
        Assert.Equal(world.Products.ByVariant[service].ProductId, mapped.ProductId);
        Assert.Equal(1m, mapped.Quantity);
        Assert.Equal(ShopifyPricing.UnitPriceWithVat(5_000m, true, 19, "COP"), mapped.UnitPrice);
        Assert.Equal(19, mapped.TaxPercentage);
        var request = Assert.Single(world.CustomLines.Requests);
        Assert.Equal(ConnectionId, request.ConnectionId);
        Assert.Equal([Garantia], request.Titles);
        Assert.Equal(["CAM-001"], Assert.Single(world.Products.Requests));
        Assert.Equal([service], Assert.Single(world.Products.VariantRequests));
    }

    // Una línea personalizada con descuento de Shopify se precia con la misma regla que una de SKU: el mismo porcentaje y el mismo
    // precio unitario que una línea equivalente (mismo precio, cantidad, descuento e IVA).
    [Fact]
    public async Task AMappedCustomLineWithADiscountGetsTheSameDiscountPercentageAsAnEquivalentSkuLine()
    {
        var world = new ImporterWorld();
        var service = Guid.CreateVersion7();
        world.Products.WithVariant(service, taxPercentage: 19);
        world.CustomLines.Map(ConnectionId, Garantia, service);
        var snapshot = Order(lines:
        [
            new ShopifyLine("CAM-001", 2m, 5_000m, 1_000m),
            new ShopifyLine(null, 2m, 5_000m, 1_000m, Garantia, true),
        ]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        Assert.Equal(ShopifyImportState.Imported, outcome.State);
        var (draft, _) = Assert.Single(world.Writer.Creates);
        var (sku, custom) = (draft.Lines[0], draft.Lines[1]);
        Assert.NotEqual(0m, custom.DiscountPercentage);
        Assert.Equal((sku.DiscountPercentage, sku.UnitPrice, sku.Quantity), (custom.DiscountPercentage, custom.UnitPrice, custom.Quantity));
    }

    [Fact]
    public async Task ACustomLineWithoutMappingWaitsWithItsTitle()
    {
        var world = new ImporterWorld();
        var snapshot = Order(lines: [Custom(Garantia)]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        AssertWaiting(row, outcome, """{"skus":[],"customTitles":["Garantía por 1 año + Envío Prioritario🚛"]}""");
        Assert.Empty(world.Writer.Creates);
        Assert.Empty(world.Products.Requests);
    }

    [Fact]
    public async Task ACustomLineMappedToAnInactiveVariantWaits()
    {
        var world = new ImporterWorld();
        var service = Guid.CreateVersion7();
        world.Products.WithVariant(service, active: false);
        world.CustomLines.Map(ConnectionId, Garantia, service);
        var snapshot = Order(lines: [Custom(Garantia)]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        AssertWaiting(row, outcome, """{"skus":[],"customTitles":["Garantía por 1 año + Envío Prioritario🚛"]}""");
        Assert.Empty(world.Writer.Creates);
    }

    [Fact]
    public async Task ACustomLineMappedToADeletedVariantWaits()
    {
        var world = new ImporterWorld();
        world.CustomLines.Map(ConnectionId, Garantia, Guid.CreateVersion7());
        var snapshot = Order(lines: [Custom(Garantia)]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        AssertWaiting(row, outcome, """{"skus":[],"customTitles":["Garantía por 1 año + Envío Prioritario🚛"]}""");
        Assert.Empty(world.Writer.Creates);
    }

    // Review Focus 3: sólo una línea marcada personalizada casa por nombre.
    [Fact]
    public async Task ARealProductLineWithoutSkuNeverMatchesByTitle()
    {
        var world = new ImporterWorld();
        var service = Guid.CreateVersion7();
        world.Products.WithVariant(service);
        world.CustomLines.Map(ConnectionId, "Camiseta", service);
        var snapshot = Order(lines: [new ShopifyLine(null, 1m, 50_000m, 0m, "Camiseta", false)]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        AssertWaiting(row, outcome, """{"skus":[null],"customTitles":[]}""");
        Assert.Empty(world.CustomLines.Requests);
    }

    // Una custom jamás tiene SKU: una línea marcada personalizada con SKU va por SKU y no casa por nombre.
    [Fact]
    public async Task ALineMarkedCustomButWithASkuGoesBySkuAndNotByTitle()
    {
        var world = new ImporterWorld();
        var service = Guid.CreateVersion7();
        world.Products.WithVariant(service);
        world.CustomLines.Map(ConnectionId, "Camiseta", service);
        var snapshot = Order(lines: [new ShopifyLine("SIN-CATALOGO", 1m, 50_000m, 0m, "Camiseta", true)]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        AssertWaiting(row, outcome, """{"skus":["SIN-CATALOGO"],"customTitles":[]}""");
        Assert.Empty(world.CustomLines.Requests);
    }

    // Review Focus 5.
    [Fact]
    public async Task AMappingOfAnotherStoreDoesNotApply()
    {
        var world = new ImporterWorld();
        var service = Guid.CreateVersion7();
        world.Products.WithVariant(service);
        world.CustomLines.Map(Guid.CreateVersion7(), Garantia, service);
        var snapshot = Order(lines: [Custom(Garantia)]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        AssertWaiting(row, outcome, """{"skus":[],"customTitles":["Garantía por 1 año + Envío Prioritario🚛"]}""");
        Assert.Equal(ConnectionId, Assert.Single(world.CustomLines.Requests).ConnectionId);
    }

    [Fact]
    public async Task MissingSkuAndMissingTitleAreBothListed()
    {
        var world = new ImporterWorld();
        var snapshot = Order(lines: [new ShopifyLine("X-9", 1m, 1_000m, 0m), Custom(Garantia), Custom(Garantia), Custom(" Otra ")]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        AssertWaiting(
            row, outcome, """{"skus":["X-9"],"customTitles":["Garantía por 1 año + Envío Prioritario🚛","Otra"]}""");
        Assert.Equal(["Garantía por 1 año + Envío Prioritario🚛", "Otra"], Assert.Single(world.CustomLines.Requests).Titles);
    }

    [Fact]
    public async Task ACustomLineWithoutTitleIsListedAsANullSku()
    {
        var world = new ImporterWorld();
        var snapshot = Order(lines: [Custom(null)]);
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        AssertWaiting(row, outcome, """{"skus":[null],"customTitles":[]}""");
        Assert.Empty(world.CustomLines.Requests);
    }

    [Fact]
    public async Task AnOrderWithoutCustomLinesDoesNotReadMappings()
    {
        var world = new ImporterWorld();
        var snapshot = Order();
        var row = world.RowFor(snapshot);

        var outcome = await world.ImportAsync(row, snapshot);

        Assert.Equal(ShopifyImportState.Imported, outcome.State);
        Assert.Empty(world.CustomLines.Requests);
        Assert.Empty(world.Products.VariantRequests);
    }

    // Se compara el JSON y no el texto: System.Text.Json escapa siempre los caracteres fuera del plano básico (el emoji sale como
    // un par sustituto \uXXXX), y es el mismo valor para quien lo lee. Las dos llaves van siempre y en ese orden.
    private static void AssertWaiting(ShopifyOrderMirror row, ShopifyImportOutcome outcome, string detail)
    {
        Assert.Equal(ShopifyImportState.Waiting, outcome.State);
        Assert.Equal(ShopifyWaitReasons.ProductUnmatched, row.WaitReason);
        var actual = JsonNode.Parse(row.WaitDetail!)!.AsObject();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(detail), actual), $"wait_detail was {row.WaitDetail}");
        Assert.Equal(["skus", "customTitles"], actual.Select(pair => pair.Key).ToArray());
    }
}
