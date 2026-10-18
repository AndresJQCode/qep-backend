using System.Reflection;
using HandledTopics = Modules.Shopify.Application.ShopifyWebhookTopics;
using RegisteredTopics = Modules.Integrations.Application.Shopify.ShopifyWebhookTopics;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-15 §5.1 y §5.5: Shopify no referencia Integrations, así que los topics que el enlace registra en cada tienda
/// (<see cref="RegisteredTopics.Registered"/>) y los que el módulo Shopify atiende (<see cref="HandledTopics"/>) son dos listas.
/// Un topic registrado que Shopify no atiende llega, responde 200 y termina como <c>unsupported_topic</c> sin efecto; uno que
/// Shopify atiende y nadie registra no llega nunca. Este proyecto ve los dos módulos: si una lista cambia sin la otra, esta
/// prueba se rompe. Los tres de cumplimiento no los registra el enlace: los configura el owner en la app de Shopify.
/// </summary>
public sealed class ShopifyWebhookTopicsParityTests
{
    private static readonly string[] Compliance =
        [HandledTopics.CustomersDataRequest, HandledTopics.CustomersRedact, HandledTopics.ShopRedact];

    [Fact]
    public void TheTopicsTheLinkRegistersAreTheOnesShopifyHandles() =>
        Assert.Equal(RegisteredTopics.Registered.Order(StringComparer.Ordinal), HandledByLinkRegistration());

    /// <summary>Las constantes de <see cref="HandledTopics"/>, sin las de cumplimiento: así una constante nueva entra sola.</summary>
    private static IEnumerable<string> HandledByLinkRegistration() =>
        typeof(HandledTopics)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Except(Compliance, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
}
