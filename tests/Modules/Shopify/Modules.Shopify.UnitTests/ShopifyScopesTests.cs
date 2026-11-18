using Modules.Shopify.Application;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-11-17 §5.1: <c>write_products</c> cuenta como <c>read_products</c>, la misma regla que el canje.</summary>
public sealed class ShopifyScopesTests
{
    [Theory]
    [InlineData(new[] { "read_orders", "read_products" }, true)]
    [InlineData(new[] { "write_products" }, true)]
    [InlineData(new[] { "read_orders", "write_orders", "read_customers" }, false)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "READ_PRODUCTS" }, false)]
    public void ScopesWithWriteProductsCanReadProducts(string[] granted, bool expected) =>
        Assert.Equal(expected, ShopifyScopes.CanReadProducts(granted));

    [Fact]
    public void TheConnectionRefHasNoScopesByDefault()
    {
        var connection = new ShopifyConnectionRef(Guid.CreateVersion7(), Guid.CreateVersion7(), "1", "a.myshopify.com", true, "t");

        Assert.Empty(connection.Scopes);
        Assert.False(ShopifyScopes.CanReadProducts(connection.Scopes));
    }
}
