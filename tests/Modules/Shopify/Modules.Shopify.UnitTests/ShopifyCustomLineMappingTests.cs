using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>El título de una línea personalizada se guarda y se compara recortado, ordinal: sensible a mayúsculas y emojis, sin
/// otra normalización.</summary>
public sealed class ShopifyCustomLineMappingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static ShopifyCustomLineMapping Create(string title) =>
        ShopifyCustomLineMapping.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), title, Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

    [Fact]
    public void CreateTrimsTheTitleAndKeepsCaseAndEmoji()
    {
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var createdBy = Guid.CreateVersion7();

        var mapping = ShopifyCustomLineMapping.Create(
            tenantId, connectionId, "  Garantía por 1 año + Envío Prioritario en este Pedido🚛  ", variantId, createdBy, Now);

        Assert.Equal("Garantía por 1 año + Envío Prioritario en este Pedido🚛", mapping.Title);
        Assert.NotEqual(Guid.Empty, mapping.Id);
        Assert.Equal((tenantId, connectionId, variantId, createdBy, Now), (mapping.TenantId, mapping.ConnectionId, mapping.VariantId, mapping.CreatedBy, mapping.CreatedAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateWithoutTitleThrows(string title) =>
        Assert.ThrowsAny<ArgumentException>(() => Create(title));

    [Fact]
    public void CreateWithATitleOf256Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => Create(new string('a', 256)));

        var mapping = Create("  " + new string('a', 255) + "  ");

        Assert.Equal(255, mapping.Title.Length);
    }

    [Fact]
    public void CreateWithAnEmptyIdThrows()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            ShopifyCustomLineMapping.Create(Guid.Empty, Guid.CreateVersion7(), "X", Guid.CreateVersion7(), Guid.CreateVersion7(), Now));
        Assert.ThrowsAny<ArgumentException>(() =>
            ShopifyCustomLineMapping.Create(Guid.CreateVersion7(), Guid.Empty, "X", Guid.CreateVersion7(), Guid.CreateVersion7(), Now));
        Assert.ThrowsAny<ArgumentException>(() =>
            ShopifyCustomLineMapping.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "X", Guid.Empty, Guid.CreateVersion7(), Now));
        Assert.ThrowsAny<ArgumentException>(() =>
            ShopifyCustomLineMapping.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "X", Guid.CreateVersion7(), Guid.Empty, Now));
    }

    [Fact]
    public void NormalizeTitleReturnsNullForBlank()
    {
        Assert.Null(ShopifyCustomLineMapping.NormalizeTitle(null));
        Assert.Null(ShopifyCustomLineMapping.NormalizeTitle(string.Empty));
        Assert.Null(ShopifyCustomLineMapping.NormalizeTitle("  "));
        Assert.Equal("X", ShopifyCustomLineMapping.NormalizeTitle(" X "));
        Assert.Equal("x", ShopifyCustomLineMapping.NormalizeTitle("x"));
    }
}
