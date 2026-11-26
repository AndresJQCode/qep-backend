using RegisteredTopics = Modules.Integrations.Application.Shopify.ShopifyWebhookTopics;
using ShopifyModuleScopes = Modules.Shopify.Application.ShopifyScopes;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-17 §5.1 y P10: la regla «puede leer productos» vive dos veces, porque Shopify no referencia Integrations. El
/// registro de webhooks (<see cref="RegisteredTopics.CanReadProducts"/>) decide si pide <c>products/*</c>; el módulo Shopify
/// (<see cref="ShopifyModuleScopes.CanReadProducts"/>) decide si importa o responde <c>scope_missing</c>. Si discrepan, una tienda
/// importa sin recibir los webhooks que la mantienen, o pasa a <c>NeedsAttention</c> por topics que no puede pedir. Este proyecto
/// ve los dos módulos: si una regla cambia sin la otra, esta prueba se rompe. Los scopes van separados por coma, como los guarda
/// la conexión.
/// </summary>
public sealed class ShopifyProductScopeParityTests
{
    [Theory]
    [InlineData("")]
    [InlineData("read_orders,read_customers")]
    [InlineData("read_products")]
    [InlineData("write_products")]
    [InlineData("read_orders,write_products")]
    [InlineData("READ_PRODUCTS")]
    public void TheScopeRuleMatchesOnBothSides(string scopes)
    {
        var granted = scopes.Split(',', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(ShopifyModuleScopes.CanReadProducts(granted), RegisteredTopics.CanReadProducts(granted));
        Assert.Equal(ShopifyModuleScopes.Parse(scopes), RegisteredTopics.ParseScopes(scopes));
    }
}
