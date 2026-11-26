using Modules.Shopify.Application;
using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-11-14 §2 y plan D4: la proyección son los valores que QEP escribió, comparados en forma QEP.</summary>
public sealed class ShopifyOrderProjectionTests
{
    private static readonly Guid Variant = Guid.Parse("01900000-0000-7000-8000-0000000000f1");
    private static readonly Guid Product = Guid.Parse("01900000-0000-7000-8000-0000000000f2");
    private static readonly Guid Department = Guid.Parse("01900000-0000-7000-8000-0000000000f3");
    private static readonly Guid City = Guid.Parse("01900000-0000-7000-8000-0000000000f4");

    [Fact]
    public void SameLinesIgnoresOrderAndDecimalScale()
    {
        var other = Guid.CreateVersion7();
        IReadOnlyList<ShopifyOrderLineState> a =
        [
            new(Variant, Product, 2.0m, 50_000.00m, 0m, 19),
            new(other, other, 1m, 10_000m, 5m, 19),
        ];
        IReadOnlyList<ShopifyOrderLineState> b =
        [
            new(other, other, 1.000m, 10_000.0m, 5.0000m, 19),
            new(Variant, Product, 2m, 50_000m, 0.0000m, 19),
        ];

        Assert.True(ShopifyOrderProjection.SameLines(a, b));
        Assert.False(ShopifyOrderProjection.SameLines(a, [b[1]]));
        Assert.False(ShopifyOrderProjection.SameLines(a, [b[0], b[1] with { Quantity = 3m }]));
    }

    [Fact]
    public void SamePartyIgnoresPhoneFormattingAndEmail()
    {
        var a = new ShopifyShippingFingerprint(" Laura Gómez ", "+57 300 111 2233", "a@x.co", "Cra 5 # 10-20", Department, City);
        var b = new ShopifyShippingFingerprint("Laura Gómez", "573001112233", "otro@x.co", "Cra 5 # 10-20 ", Department, City);

        Assert.True(ShopifyOrderProjection.SameParty(a, b));
        Assert.False(ShopifyOrderProjection.SameParty(a, b with { CityId = Guid.CreateVersion7() }));
        Assert.False(ShopifyOrderProjection.SameParty(a, null));
        Assert.True(ShopifyOrderProjection.SameParty(null, null));
    }

    // Review I-3: Shopify puede guardar el teléfono en E.164 aunque QEP lo haya mandado nacional. El eco tiene que salir igual.
    [Theory]
    [InlineData("3001112233", "+573001112233", true)]
    [InlineData("+57 300 111 2233", "300 111 2233", true)]
    [InlineData("6044445566", "+57 604 444 5566", true)]
    [InlineData("3001112233", "3001112234", false)]
    [InlineData("3001112233", "+13001112233000", false)]
    [InlineData("1112233", "+5730011112233", false)]
    [InlineData("2233", "+573001112233", false)]
    [InlineData("", "+573001112233", false)]
    [InlineData(null, null, true)]
    public void SamePartyComparesPhonesIgnoringTheCountryCode(string? a, string? b, bool expected)
    {
        var party = new ShopifyShippingFingerprint("Laura Gómez", a, "a@x.co", "Cra 5 # 10-20", Department, City);

        Assert.Equal(expected, ShopifyOrderProjection.SameParty(party, party with { Phone = b }));
        Assert.Equal(expected, ShopifyOrderProjection.SameParty(party with { Phone = b }, party));
    }

    // El código postal es parte de la dirección: una edición de Shopify que sólo lo cambia no es un eco.
    [Fact]
    public void SamePartyComparesThePostalCode()
    {
        var party = new ShopifyShippingFingerprint("Laura Gómez", "3001112233", "a@x.co", "Cra 5 # 10-20", Department, City, "050021");

        Assert.True(ShopifyOrderProjection.SameParty(party, party with { PostalCode = " 050021 " }));
        Assert.False(ShopifyOrderProjection.SameParty(party, party with { PostalCode = "055422" }));
        Assert.False(ShopifyOrderProjection.SameParty(party, party with { PostalCode = null }));
        Assert.True(ShopifyOrderProjection.SameParty(party with { PostalCode = null }, party with { PostalCode = " " }));
    }

    // Fuera de Colombia la ciudad es país, nombre y región: cambiar cualquiera es otra dirección.
    [Fact]
    public void SamePartyComparesTheForeignCity()
    {
        var region = Guid.CreateVersion7();
        var party = new ShopifyShippingFingerprint(
            "Lucía", "+34600111222", "a@x.es", "Calle Mayor 1", null, null, "28001", "ES", "Madrid", region);

        Assert.True(ShopifyOrderProjection.SameParty(party, party with { CityName = " Madrid " }));
        Assert.False(ShopifyOrderProjection.SameParty(party, party with { Country = "PT" }));
        Assert.False(ShopifyOrderProjection.SameParty(party, party with { CityName = "Getafe" }));
        Assert.False(ShopifyOrderProjection.SameParty(party, party with { RegionId = null }));
    }

    // Una proyección guardada antes de que la huella llevara país, ciudad escrita y región (shopify.orders.applied_projection).
    [Fact]
    public void AnOldProjectionJsonWithoutTheNewFieldsStillDeserializes()
    {
        var json = $$$"""
            {"lines":[],"shipping":{"isStorePickup":false,"carrierId":null,"carrierName":null,"chargeMode":null,"freightAmount":0,
             "freightTaxPercentage":0,"cashOnDelivery":false},
             "shippingParty":{"name":"Laura","phone":"3001112233","email":"laura@x.co","address":"Cra 5",
             "departmentId":"{{{Department}}}","cityId":"{{{City}}}"},"buyerNotes":null,"document":null,"attributes":{}}
            """;

        var projection = ShopifyOrderProjection.Read(json);

        Assert.Equal(
            new ShopifyShippingFingerprint("Laura", "3001112233", "laura@x.co", "Cra 5", Department, City),
            projection.ShippingParty);
    }

    [Fact]
    public void WriteAndReadRoundTrip()
    {
        var projection = new ShopifyOrderProjection(
            [new ShopifyOrderLineState(Variant, Product, 2m, 50_000m, 10m, 19)],
            new ShopifyShippingDraft(false, Guid.CreateVersion7(), "Servientrega", "Charged", 15_000m, 19, true),
            new ShopifyShippingFingerprint("Laura", "3001112233", "laura@x.co", "Cra 5", Department, City),
            "Dejar en portería",
            new ShopifyBuyerDocument("CC", "1036000111"),
            new Dictionary<string, string> { ["Documento"] = "1036000111" });

        var json = ShopifyOrderProjection.Write(projection);
        var read = ShopifyOrderProjection.Read(json);

        Assert.Contains("\"buyerNotes\":\"Dejar en portería\"", json, StringComparison.Ordinal);
        Assert.True(ShopifyOrderProjection.SameLines(projection.Lines, read.Lines));
        Assert.Equal(projection.Shipping, read.Shipping);
        Assert.Equal(projection.ShippingParty, read.ShippingParty);
        Assert.Equal((projection.BuyerNotes, projection.Document), (read.BuyerNotes, read.Document));
        Assert.Equal(projection.Attributes, read.Attributes);
    }

    [Fact]
    public void FromTakesTheOrderValuesAndTheGivenDocumentAndAttributes()
    {
        var order = new ShopifyOrderState(
            Guid.CreateVersion7(), 3, "Open", true, false, false, "PaymentPending", Guid.CreateVersion7(), "COP", false,
            [new ShopifyOrderLineState(Variant, Product, 1m, 119_000m, 0m, 19)],
            new ShopifyShippingDraft(false, null, null, null, 0m, 0, false),
            null, null, "Nota");
        var attributes = new Dictionary<string, string> { ["k"] = "v" };

        var projection = ShopifyOrderProjection.From(order, null, attributes);

        Assert.Equal((order.Lines, order.Shipping, (ShopifyShippingFingerprint?)null, "Nota"), (projection.Lines, projection.Shipping, projection.ShippingParty, projection.BuyerNotes));
        Assert.Equal(attributes, projection.Attributes);
    }
}
