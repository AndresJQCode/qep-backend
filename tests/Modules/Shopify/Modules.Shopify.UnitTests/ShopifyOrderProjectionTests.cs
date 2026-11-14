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
