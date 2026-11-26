using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-11-14 §3: el diff por grupo entre dos versiones de Shopify, armado para pintar (BFF): textos, números crudos
/// y booleanos, sin enums de QEP. Pago, cancelación y <c>updated_at</c> no son parte de ningún grupo (§6.3).</summary>
public sealed class ShopifyOrderDiffTests
{
    private static readonly ShopifyAddress Address = new(
        "Laura Gómez", "Cra 5 # 10-20", "Apto 301", "Medellin", "Antioquia", "ANT", "CO", "3001112233", "1036000111");

    private static ShopifyLine Shirt(decimal quantity = 2m, long? id = 9001, decimal price = 50_000m) =>
        new("CAM-001", quantity, price, 0m, "Camiseta", false, id, 2m);

    private static ShopifyOrderSnapshot Snapshot(
        IReadOnlyList<ShopifyLine>? lines = null,
        ShopifyAddress? address = null,
        string? note = "Dejar en portería",
        IReadOnlyDictionary<string, string>? attributes = null,
        IReadOnlyList<ShopifyShippingLine>? shippingLines = null,
        ShopifyBuyer? buyer = null) =>
        ImporterWorld.Order(
            lines: lines ?? [Shirt()],
            address: address ?? Address,
            note: note,
            attributes: attributes,
            shippingLines: shippingLines ?? [new ShopifyShippingLine("standard", "Envío estándar", 15_000m, 0m, false)],
            buyer: buyer);

    private static ShopifyStore Store(string? documentKey = null)
    {
        var world = new ImporterWorld();
        if (documentKey is not null)
        {
            world.Store.Configure(
                ImporterWorld.AdvisorId,
                new ShopifyBillingAccount(ImporterWorld.CompanyId, "Bancolombia", "123-456", "COP"),
                null,
                documentKey,
                ShopifyStore.DefaultDocumentTypeAttributeKey,
                ShopifyStore.DefaultIdentificationTypeCode,
                null,
                ShopifyEditSync.Manual,
                ShopifyPaymentStatuses.All,
                ImporterWorld.AdvisorId,
                ImporterWorld.Now);
        }

        return world.Store;
    }

    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Json(JsonNode? node) => node?.ToJsonString(Readable) ?? "null";

    [Fact]
    public void TheSameSnapshotHasNoDiff()
    {
        Assert.Empty(ShopifyOrderDiff.Compute(Snapshot(), Snapshot(), Store()));
    }

    // Sin la clave "zip" (payload guardado antes de que el espejo la leyera) no se sabe cuál había: no hay fila.
    [Fact]
    public void AZipOverAPayloadWithoutTheKeyHasNoRow()
    {
        var legacy = Snapshot(address: Address with { ZipUnrecorded = true });
        var current = Snapshot(address: Address with { Zip = "050021" });

        Assert.Empty(ShopifyOrderDiff.Compute(legacy, current, Store()));
        Assert.Empty(ShopifyOrderDiff.Compute(current, legacy, Store()));
        Assert.Equal(
            "zip",
            Assert.Single(Assert.Single(ShopifyOrderDiff.Compute(Snapshot(), current, Store())).Rows).Field);
    }

    [Fact]
    public void APayloadWithoutTheZipKeyKeepsItThatWay()
    {
        var node = JsonNode.Parse(ShopifyOrderIngestion.WritePayload(Snapshot()))!;
        Assert.True(node["shippingAddress"]!.AsObject().ContainsKey("zip"));
        var recorded = ShopifyOrderIngestion.ReadPayload(node.ToJsonString());
        node["shippingAddress"]!.AsObject().Remove("zip");

        var legacy = ShopifyOrderIngestion.ReadPayload(node.ToJsonString());
        var rewritten = JsonNode.Parse(ShopifyOrderIngestion.WritePayload(legacy))!;

        Assert.False(recorded.ShippingAddress!.ZipUnrecorded);
        Assert.True(legacy.ShippingAddress!.ZipUnrecorded);
        Assert.False(rewritten["shippingAddress"]!.AsObject().ContainsKey("zip"));
        Assert.Equal(legacy.ShippingAddress, ShopifyOrderIngestion.ReadPayload(rewritten.ToJsonString()).ShippingAddress);
    }

    [Fact]
    public void OnlyPaymentCancellationAndUpdatedAtAreNoDiff()
    {
        var after = Snapshot() with
        {
            FinancialStatus = "paid",
            CancelledAt = ImporterWorld.Now,
            CancelReason = "customer",
            UpdatedAt = ImporterWorld.Now.AddHours(3),
            TotalPrice = 1m,
        };

        Assert.Empty(ShopifyOrderDiff.Compute(Snapshot(), after, Store()));
    }

    [Fact]
    public void AQuantityChangeIsOneLineRowPairedById()
    {
        // La línea cambia de posición y de cantidad: se empareja por id, no por posición.
        var other = new ShopifyLine("GOR-001", 1m, 30_000m, 0m, "Gorra", false, 9002, 1m);
        var before = Snapshot(lines: [Shirt(), other]);
        var after = Snapshot(lines: [other, Shirt(quantity: 1m)]);

        var group = Assert.Single(ShopifyOrderDiff.Compute(before, after, Store()));

        Assert.Equal(ShopifyEditGroupKind.Lines, group.Kind);
        var row = Assert.Single(group.Rows);
        Assert.Equal(("line", "Camiseta (CAM-001)"), (row.Field, row.Label));
        Assert.Equal("""{"sku":"CAM-001","title":"Camiseta","quantity":2,"unitPrice":50000,"discountAmount":0}""", Json(row.Before));
        Assert.Equal("""{"sku":"CAM-001","title":"Camiseta","quantity":1,"unitPrice":50000,"discountAmount":0}""", Json(row.After));
    }

    [Fact]
    public void ARemovedLineHasAfterNullAndANewLineHasBeforeNull()
    {
        var added = new ShopifyLine("GOR-001", 1m, 30_000m, 0m, "Gorra", false, 9002, 1m);
        var removed = Shirt(quantity: 0m);

        var rows = Assert.Single(ShopifyOrderDiff.Compute(Snapshot(), Snapshot(lines: [removed, added]), Store())).Rows;

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Label == "Camiseta (CAM-001)" && row.Before is not null && row.After is null);
        Assert.Contains(rows, row => row.Label == "Gorra (GOR-001)" && row.Before is null && row.After is not null);
    }

    [Fact]
    public void ACustomLineIsLabelledByItsTitle()
    {
        var custom = new ShopifyLine(null, 1m, 5_000m, 0m, "Garantía🚛", true, 9003, 1m);

        var row = Assert.Single(Assert.Single(ShopifyOrderDiff.Compute(Snapshot(), Snapshot(lines: [Shirt(), custom]), Store())).Rows);

        Assert.Equal("Garantía🚛", row.Label);
    }

    [Fact]
    public void LinesWithoutIdsPairByPosition()
    {
        var before = Snapshot(lines: [Shirt(id: null), new ShopifyLine("GOR-001", 1m, 30_000m, 0m, "Gorra")]);
        var after = Snapshot(lines: [Shirt(id: null), new ShopifyLine("GOR-001", 3m, 30_000m, 0m, "Gorra")]);

        var row = Assert.Single(Assert.Single(ShopifyOrderDiff.Compute(before, after, Store())).Rows);

        Assert.Equal("Gorra (GOR-001)", row.Label);
        Assert.Equal(3m, row.After!["quantity"]!.GetValue<decimal>());
    }

    [Fact]
    public void ALegacyPayloadWithoutLineIdsAgainstANewSnapshotHasNoLinesDiff()
    {
        // Un payload guardado antes del spec no trae el id de la línea; el siguiente webhook sí. Las mismas líneas no son un cambio.
        var before = Snapshot(lines: [Shirt(id: null), new ShopifyLine("GOR-001", 1m, 30_000m, 0m, "Gorra")]);
        var after = Snapshot(lines: [Shirt(), new ShopifyLine("GOR-001", 1m, 30_000m, 0m, "Gorra", false, 9002, 1m)]);

        Assert.Empty(ShopifyOrderDiff.Compute(before, after, Store()));
    }

    [Fact]
    public void AShippingAmountChangeIsAShippingRow()
    {
        var after = Snapshot(shippingLines: [new ShopifyShippingLine("standard", "Envío estándar", 18_000m, 0m, false)]);

        var group = Assert.Single(ShopifyOrderDiff.Compute(Snapshot(), after, Store()));

        Assert.Equal(ShopifyEditGroupKind.Shipping, group.Kind);
        var row = Assert.Single(group.Rows);
        Assert.Equal(("amount", (string?)null, "15000", "18000"), (row.Field, row.Label, Json(row.Before), Json(row.After)));
    }

    [Fact]
    public void ACityChangeIsAShippingAddressRow()
    {
        var group = Assert.Single(ShopifyOrderDiff.Compute(Snapshot(), Snapshot(address: Address with { City = " Envigado " }), Store()));

        Assert.Equal(ShopifyEditGroupKind.ShippingAddress, group.Kind);
        var row = Assert.Single(group.Rows);
        Assert.Equal(("city", (string?)null, "\"Medellin\"", "\"Envigado\""), (row.Field, row.Label, Json(row.Before), Json(row.After)));
    }

    [Fact]
    public void TheDocumentComesFromTheConfiguredSource()
    {
        var before = Snapshot() with { BillingAddress = Address with { Company = "1036000111" } };
        var after = Snapshot() with { BillingAddress = Address with { Company = "1036999999" } };

        var group = Assert.Single(ShopifyOrderDiff.Compute(before, after, Store(ShopifyDocumentSources.BillingAddressCompany)));

        Assert.Equal(ShopifyEditGroupKind.Buyer, group.Kind);
        var row = Assert.Single(group.Rows);
        Assert.Equal(("document", "\"CC 1036000111\"", "\"CC 1036999999\""), (row.Field, Json(row.Before), Json(row.After)));
    }

    [Fact]
    public void ANoteChangeIsANoteRow()
    {
        var group = Assert.Single(ShopifyOrderDiff.Compute(Snapshot(), Snapshot(note: "Llamar antes"), Store()));

        Assert.Equal(ShopifyEditGroupKind.Note, group.Kind);
        var row = Assert.Single(group.Rows);
        Assert.Equal(("note", "\"Dejar en portería\"", "\"Llamar antes\""), (row.Field, Json(row.Before), Json(row.After)));
    }

    [Fact]
    public void AttributesListOnlyTheChangedKeys()
    {
        var before = Snapshot(attributes: new Dictionary<string, string> { ["Igual"] = "1", ["Cambia"] = "a", ["Sale"] = "x" });
        var after = Snapshot(attributes: new Dictionary<string, string> { ["Igual"] = "1", ["Cambia"] = "b", ["Entra"] = "y" });

        var group = Assert.Single(ShopifyOrderDiff.Compute(before, after, Store()));

        Assert.Equal(ShopifyEditGroupKind.Attributes, group.Kind);
        Assert.Equal(
            [("Cambia", "\"a\"", "\"b\""), ("Entra", "null", "\"y\""), ("Sale", "\"x\"", "null")],
            group.Rows.Select(row => (row.Label!, Json(row.Before), Json(row.After))).OrderBy(row => row.Item1, StringComparer.Ordinal).ToArray());
        Assert.All(group.Rows, row => Assert.Equal("attribute", row.Field));
    }

    [Fact]
    public void MoneyValuesAreRawNumbers()
    {
        var before = Snapshot(shippingLines: [new ShopifyShippingLine("standard", "Envío estándar", 12_000.00m, 0m, false)]);
        var after = Snapshot(
            lines: [Shirt(price: 45_000.50m)],
            shippingLines: [new ShopifyShippingLine("standard", "Envío estándar", 15_000.00m, 0m, false)]);

        var groups = ShopifyOrderDiff.Compute(before, after, Store());

        var amount = Assert.Single(groups.Single(group => group.Kind == ShopifyEditGroupKind.Shipping).Rows).After!;
        Assert.Equal(JsonValueKind.Number, amount.GetValueKind());
        Assert.Equal("15000", amount.ToJsonString());
        var line = Assert.Single(groups.Single(group => group.Kind == ShopifyEditGroupKind.Lines).Rows).After!;
        Assert.Equal(JsonValueKind.Number, line["unitPrice"]!.GetValueKind());
        Assert.Equal("45000.5", line["unitPrice"]!.ToJsonString());
    }

    [Fact]
    public void ALineWithoutSkuHasANullSku()
    {
        var custom = new ShopifyLine(null, 1m, 5_000m, 0m, "Garantía", true, 9003, 1m);

        var row = Assert.Single(Assert.Single(ShopifyOrderDiff.Compute(Snapshot(), Snapshot(lines: [Shirt(), custom]), Store())).Rows);

        Assert.True(row.After!.AsObject().ContainsKey("sku"));
        Assert.Null(row.After["sku"]);
        Assert.Equal("Garantía", row.Label);
    }
}
