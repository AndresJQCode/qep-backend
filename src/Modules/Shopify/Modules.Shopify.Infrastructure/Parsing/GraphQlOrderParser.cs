using System.Text.Json;
using Modules.Shopify.Application;
using static Modules.Shopify.Infrastructure.Parsing.ShopifyJson;

namespace Modules.Shopify.Infrastructure.Parsing;

/// <summary>
/// Spec 2026-10-15 §5.3: el nodo <c>Order</c> del Admin API GraphQL (conciliación y sincronización manual) a
/// <see cref="ShopifyOrderSnapshot"/>. Los ids llegan como GID (<c>gid://shopify/Order/123</c> → 123), los montos de
/// <c>*Set.shopMoney</c> y los enums en mayúsculas (<c>PAID</c> → <c>paid</c>, como REST). <c>lineItems</c> y
/// <c>shippingLines</c> se aceptan con <c>nodes</c> o con <c>edges/node</c>. El contacto del cliente sale de
/// <c>defaultEmailAddress</c>/<c>defaultPhoneNumber</c> (D-S29: 2026-10). Las líneas de envío retiradas sólo vienen si la
/// consulta pide <c>shippingLines(includeRemovals: true)</c>.
/// </summary>
public static class GraphQlOrderParser
{
    public static ShopifyOrderSnapshot Parse(JsonElement node)
    {
        EnsureObject(node, "GraphQL");
        var customer = Find(node, "customer");

        return new ShopifyOrderSnapshot(
            OptionalId(node, "id", "id", "Order") ?? throw Missing("id"),
            RequiredString(node, "name", "name"),
            customer is { } c ? OptionalId(c, "id", "customer.id", "Customer") : null,
            RequiredDate(node, "createdAt", "createdAt"),
            RequiredDate(node, "updatedAt", "updatedAt"),
            OptionalDate(node, "cancelledAt", "cancelledAt"),
            Enum(OptionalString(node, "cancelReason", "cancelReason")),
            Enum(OptionalString(node, "displayFinancialStatus", "displayFinancialStatus")) ?? string.Empty,
            RequiredString(node, "currencyCode", "currencyCode"),
            OptionalAmount(node, "totalPriceSet.shopMoney.amount", "totalPriceSet.shopMoney.amount")
                ?? throw Missing("totalPriceSet.shopMoney.amount"),
            Boolean(node, "taxesIncluded", "taxesIncluded"),
            Boolean(node, "test", "test"),
            OptionalString(node, "note", "note"),
            Strings(node, "paymentGatewayNames", "paymentGatewayNames"),
            Buyer(node, customer),
            Find(node, "shippingAddress") is { } address ? Address(address) : null,
            Attributes(node, "customAttributes", "key", "customAttributes"),
            Lines(node),
            ShippingLines(node));
    }

    // Customer.email y Customer.phone están deprecados en la 2026-10 (D-S29): el contacto del cliente viene en
    // defaultEmailAddress.emailAddress y defaultPhoneNumber.phoneNumber, que es lo que pide la consulta de la conciliación.
    private static ShopifyBuyer Buyer(JsonElement order, JsonElement? customer) => new(
        customer is { } c ? OptionalString(c, "firstName", "customer.firstName") : null,
        customer is { } d ? OptionalString(d, "lastName", "customer.lastName") : null,
        OptionalString(order, "email", "email") ?? CustomerContact(customer, "defaultEmailAddress", "emailAddress"),
        OptionalString(order, "phone", "phone") ?? CustomerContact(customer, "defaultPhoneNumber", "phoneNumber"));

    private static string? CustomerContact(JsonElement? customer, string objectName, string name) =>
        customer is { } c && Find(c, objectName) is { } contact ? OptionalString(contact, name, $"customer.{objectName}.{name}") : null;

    private static ShopifyAddress Address(JsonElement address) => new(
        OptionalString(address, "name", "shippingAddress.name"),
        OptionalString(address, "address1", "shippingAddress.address1"),
        OptionalString(address, "address2", "shippingAddress.address2"),
        OptionalString(address, "city", "shippingAddress.city"),
        OptionalString(address, "province", "shippingAddress.province"),
        OptionalString(address, "provinceCode", "shippingAddress.provinceCode"),
        OptionalString(address, "countryCodeV2", "shippingAddress.countryCodeV2"),
        OptionalString(address, "phone", "shippingAddress.phone"),
        OptionalString(address, "company", "shippingAddress.company"));

    private static ShopifyLine[] Lines(JsonElement order) =>
        Items(order, "lineItems", "lineItems", connection: true)
            .Select((line, index) =>
            {
                var field = $"lineItems[{index}]";
                return new ShopifyLine(
                    OptionalString(line, "sku", $"{field}.sku"),
                    Find(line, "quantity") is { } quantity ? Decimal(quantity, $"{field}.quantity") : 0m,
                    OptionalAmount(line, $"{field}.originalUnitPriceSet.shopMoney.amount", "originalUnitPriceSet.shopMoney.amount") ?? 0m,
                    Items(line, "discountAllocations", $"{field}.discountAllocations")
                        .Select((allocation, i) => OptionalAmount(allocation, $"{field}.discountAllocations[{i}].allocatedAmountSet.shopMoney.amount", "allocatedAmountSet.shopMoney.amount") ?? 0m)
                        .Sum());
            })
            .ToArray();

    private static ShopifyShippingLine[] ShippingLines(JsonElement order) =>
        Items(order, "shippingLines", "shippingLines", connection: true)
            .Select((line, index) =>
            {
                var field = $"shippingLines[{index}]";
                return new ShopifyShippingLine(
                    OptionalString(line, "code", $"{field}.code"),
                    OptionalString(line, "title", $"{field}.title"),
                    OptionalAmount(line, $"{field}.discountedPriceSet.shopMoney.amount", "discountedPriceSet.shopMoney.amount") ?? 0m,
                    Items(line, "taxLines", $"{field}.taxLines")
                        .Select((tax, i) => OptionalAmount(tax, $"{field}.taxLines[{i}].priceSet.shopMoney.amount", "priceSet.shopMoney.amount") ?? 0m)
                        .Sum(),
                    Boolean(line, "isRemoved", $"{field}.isRemoved"));
            })
            .ToArray();
}
