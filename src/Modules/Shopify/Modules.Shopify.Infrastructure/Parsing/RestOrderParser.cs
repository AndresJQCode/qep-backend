using System.Text.Json;
using Modules.Shopify.Application;
using static Modules.Shopify.Infrastructure.Parsing.ShopifyJson;

namespace Modules.Shopify.Infrastructure.Parsing;

/// <summary>
/// Spec 2026-10-15 §5.2: el cuerpo REST de los webhooks <c>orders/*</c> a <see cref="ShopifyOrderSnapshot"/>. Montos de
/// <c>*_set.shop_money</c>, con el campo plano (<c>price</c>, <c>discounted_price</c>…) de respaldo, que REST también da en la
/// moneda de la tienda. La forma de <c>shipping_lines</c> (sobre todo <c>is_removed</c>) está sin verificar contra un webhook
/// real (V-9): si falta, la línea cuenta como vigente.
/// </summary>
public static class RestOrderParser
{
    public static ShopifyOrderSnapshot Parse(JsonElement order)
    {
        EnsureObject(order, "REST");
        var customer = Find(order, "customer");

        return new ShopifyOrderSnapshot(
            OptionalId(order, "id", "id", "Order") ?? throw Missing("id"),
            RequiredString(order, "name", "name"),
            customer is { } c ? OptionalId(c, "id", "customer.id", "Customer") : null,
            RequiredDate(order, "created_at", "created_at"),
            RequiredDate(order, "updated_at", "updated_at"),
            OptionalDate(order, "cancelled_at", "cancelled_at"),
            Enum(OptionalString(order, "cancel_reason", "cancel_reason")),
            Enum(OptionalString(order, "financial_status", "financial_status")) ?? string.Empty,
            RequiredString(order, "currency", "currency"),
            OptionalAmount(order, "total_price_set.shop_money.amount", "total_price_set.shop_money.amount", "total_price")
                ?? throw Missing("total_price_set.shop_money.amount"),
            Boolean(order, "taxes_included", "taxes_included"),
            Boolean(order, "test", "test"),
            OptionalString(order, "note", "note"),
            Strings(order, "payment_gateway_names", "payment_gateway_names"),
            Buyer(order, customer),
            Find(order, "shipping_address") is { } address ? Address(address) : null,
            Attributes(order, "note_attributes", "name", "note_attributes"),
            Lines(order),
            ShippingLines(order));
    }

    private static ShopifyBuyer Buyer(JsonElement order, JsonElement? customer) => new(
        customer is { } c ? OptionalString(c, "first_name", "customer.first_name") : null,
        customer is { } d ? OptionalString(d, "last_name", "customer.last_name") : null,
        OptionalString(order, "email", "email") ?? (customer is { } e ? OptionalString(e, "email", "customer.email") : null),
        OptionalString(order, "phone", "phone") ?? (customer is { } f ? OptionalString(f, "phone", "customer.phone") : null));

    private static ShopifyAddress Address(JsonElement address) => new(
        OptionalString(address, "name", "shipping_address.name"),
        OptionalString(address, "address1", "shipping_address.address1"),
        OptionalString(address, "address2", "shipping_address.address2"),
        OptionalString(address, "city", "shipping_address.city"),
        OptionalString(address, "province", "shipping_address.province"),
        OptionalString(address, "province_code", "shipping_address.province_code"),
        OptionalString(address, "country_code", "shipping_address.country_code"),
        OptionalString(address, "phone", "shipping_address.phone"),
        OptionalString(address, "company", "shipping_address.company"));

    private static ShopifyLine[] Lines(JsonElement order) =>
        Items(order, "line_items", "line_items")
            .Select((line, index) =>
            {
                var field = $"line_items[{index}]";
                return new ShopifyLine(
                    OptionalString(line, "sku", $"{field}.sku"),
                    Find(line, "quantity") is { } quantity ? Decimal(quantity, $"{field}.quantity") : 0m,
                    OptionalAmount(line, $"{field}.price", "price_set.shop_money.amount", "price") ?? 0m,
                    Items(line, "discount_allocations", $"{field}.discount_allocations")
                        .Select((allocation, i) => OptionalAmount(allocation, $"{field}.discount_allocations[{i}].amount", "amount_set.shop_money.amount", "amount") ?? 0m)
                        .Sum());
            })
            .ToArray();

    private static ShopifyShippingLine[] ShippingLines(JsonElement order) =>
        Items(order, "shipping_lines", "shipping_lines")
            .Select((line, index) =>
            {
                var field = $"shipping_lines[{index}]";
                return new ShopifyShippingLine(
                    OptionalString(line, "code", $"{field}.code"),
                    OptionalString(line, "title", $"{field}.title"),
                    OptionalAmount(line, $"{field}.discounted_price", "discounted_price_set.shop_money.amount", "discounted_price") ?? 0m,
                    Items(line, "tax_lines", $"{field}.tax_lines")
                        .Select((tax, i) => OptionalAmount(tax, $"{field}.tax_lines[{i}].price", "price_set.shop_money.amount", "price") ?? 0m)
                        .Sum(),
                    Boolean(line, "is_removed", $"{field}.is_removed"));
            })
            .ToArray();
}
