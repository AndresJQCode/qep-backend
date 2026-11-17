using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Quotations.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.Copilot;

/// <summary>
/// Spec 2026-11-17 (copiloto) §6 y plan fase 2 C3.1: pedidos y cotizaciones. Cada herramienta despacha <b>la misma consulta</b> que su
/// endpoint (<c>OrderEndpoints</c>, <c>QuotationEndpoints</c>) en el scope delegado, así que el handler aplica sus reglas de alcance como si
/// el miembro hubiera llamado. El detalle es una proyección: sin comprobantes ni archivos, sin PDF, sin la cuenta bancaria, sin las partes
/// (teléfonos, correos, direcciones) y sin la nota del comprador, que es texto de un tercero (D-C10). Lo que el modelo no necesita para
/// contestar no viaja.
/// </summary>
internal static class CopilotDocumentTools
{
    public static IReadOnlyList<ICopilotTool> All() =>
    [
        new SearchOrdersTool(),
        new GetOrderTool(),
        new SearchQuotationsTool(),
        new GetQuotationTool(),
    ];

    public static CopilotCitation OrderRef(Guid id, string number) => new(CopilotCitationKinds.Order, id.ToString(), number);

    public static CopilotCitation QuotationRef(Guid id, string number) => new(CopilotCitationKinds.Quotation, id.ToString(), number);

    /// <summary>Las líneas del documento con el nombre congelado del producto; sin descuentos por escala ni orígenes, que no cambian la
    /// respuesta a «qué lleva».</summary>
    public static object Items(IEnumerable<QuotationItemDto> items) =>
        items.OrderBy(item => item.Position).Select(item => new
        {
            item.ProductId,
            item.VariantId,
            item.ProductCode,
            item.ProductName,
            item.Quantity,
            item.UnitPrice,
            item.DiscountPercentage,
            item.Subtotal,
            item.TaxPercentage,
            item.TaxAmount,
        }).ToList();

    public static object Shipping(ShippingDto shipping) => new
    {
        shipping.IsStorePickup,
        carrier = shipping.Carrier?.Name,
        shipping.ChargeMode,
        shipping.CashOnDelivery,
        shipping.FreightSubtotal,
        shipping.FreightTaxAmount,
    };
}

internal sealed class SearchOrdersTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "search_orders",
        "Busca pedidos, 20 por página, por fecha del pedido, cliente, asesor, estado, estado de pago, origen o número. Sin estado no salen los archivados. Sirve para encontrar pedidos concretos; nunca sumes sus filas: los totales salen de get_orders_summary.",
        OrdersPermissions.OrderRead,
        TenantModuleKeys.Orders,
        CopilotReportTools.Period,
        """
        "clientId":{"type":["string","null"],"description":"Id del cliente (uuid) o null."},"advisorId":{"type":["string","null"],"description":"Id del asesor (uuid) o null."},"status":{"type":["string","null"],"description":"Open, Archived, Cancelled o null."},"paymentStatus":{"type":["string","null"],"description":"FullPaymentReceived, PartialPaymentReceived, PaymentPending o null."},"source":{"type":["string","null"],"description":"Quotation, Manual, Shopify, Pos, Store o null."},"orderNumber":{"type":["string","null"],"description":"El número del pedido, o parte de él, o null."}
        """,
        CopilotReportTools.Page);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var (from, to) = CopilotArguments.Period(call.Arguments);
        var page = await call.Dispatcher.QueryAsync(
            new ListOrdersQuery(
                call.Principal.TenantId,
                CopilotArguments.Id(call.Arguments, "clientId"),
                CopilotArguments.Id(call.Arguments, "advisorId"),
                CopilotArguments.Code(call.Arguments, "status"),
                CopilotArguments.Code(call.Arguments, "paymentStatus"),
                from,
                to,
                ClientCuc: null,
                CopilotArguments.Search(call.Arguments, "orderNumber"),
                CopilotArguments.Code(call.Arguments, "source"),
                CopilotArguments.Page(call.Arguments),
                CopilotReportTools.PageSize),
            cancellationToken);
        var list = CopilotReportList<object>.Of(page.Items, page.Total, page.Page, item => new
        {
            @ref = CopilotDocumentTools.OrderRef(item.Id, item.OrderNumber),
            item.OrderNumber,
            item.OrderDate,
            item.Source,
            item.QuotationNumber,
            client = item.ClientName,
            item.ClientId,
            advisor = item.AdvisorName,
            item.Status,
            item.PaymentStatus,
            item.FulfillmentStatus,
            item.Invoiced,
            item.Currency,
            item.Total,
        });
        return CopilotToolResult.Ok(
            list, [.. page.Items.Take(CopilotReportTools.PageSize).Select(item => CopilotDocumentTools.OrderRef(item.Id, item.OrderNumber))]);
    }
}

internal sealed class GetOrderTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "get_order",
        "El detalle de un pedido: estados, totales, saldo por pagar, líneas con su producto, envío y las fechas de su historial. No trae comprobantes, archivos ni datos bancarios.",
        OrdersPermissions.OrderRead,
        TenantModuleKeys.Orders,
        """
        "orderId":{"type":"string","description":"Id del pedido (uuid), tomado de una búsqueda."}
        """);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var detail = await call.Dispatcher.QueryAsync(
            new GetOrderByIdQuery(call.Principal.TenantId, CopilotArguments.RequiredId(call.Arguments, "orderId")), cancellationToken);
        var order = detail.Order;
        var reference = CopilotDocumentTools.OrderRef(order.Id, order.OrderNumber);
        List<CopilotCitation> refs = [reference];
        if (order.QuotationId is { } quotationId && detail.QuotationNumber is { } quotationNumber)
        {
            refs.Add(CopilotDocumentTools.QuotationRef(quotationId, quotationNumber));
        }

        return CopilotToolResult.Ok(
            new
            {
                @ref = reference,
                order.OrderNumber,
                order.OrderDate,
                order.Source,
                order.ExternalReference,
                order.QuotationId,
                detail.QuotationNumber,
                order.ClientId,
                order.AdvisorId,
                order.Status,
                order.PaymentStatus,
                order.FulfillmentStatus,
                order.Invoiced,
                order.PaymentMethod,
                order.Currency,
                order.Subtotal,
                order.TaxAmount,
                order.DiscountAmount,
                order.FreightSubtotal,
                order.FreightTaxAmount,
                order.Total,
                order.RetentionAmount,
                order.NetTotal,
                order.Balance,
                order.Notes,
                order.CancellationReason,
                order.ConvertedAt,
                order.PaidMarkedAt,
                order.PreparedAt,
                order.InvoicedAt,
                order.ArchivedAt,
                order.CancelledAt,
                items = CopilotDocumentTools.Items(order.Items),
                shipping = CopilotDocumentTools.Shipping(order.Shipping),
                history = detail.History.Select(entry => new { entry.EventType, entry.EventAt }).ToList(),
            },
            refs);
    }
}

internal sealed class SearchQuotationsTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "search_quotations",
        "Busca cotizaciones, 20 por página, por fecha de creación, cliente, asesor, estado o número. Sirve para encontrar cotizaciones concretas; nunca sumes sus filas: los totales salen de get_quotations_summary.",
        QuotationsPermissions.QuotationRead,
        TenantModuleKeys.Quotations,
        CopilotReportTools.Period,
        """
        "clientId":{"type":["string","null"],"description":"Id del cliente (uuid) o null."},"advisorId":{"type":["string","null"],"description":"Id del asesor (uuid) o null."},"status":{"type":["string","null"],"description":"Draft, Sent, Expired, Voided, Converted o null."},"quotationNumber":{"type":["string","null"],"description":"El número de la cotización, o parte de él, o null."}
        """,
        CopilotReportTools.Page);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var (from, to) = CopilotArguments.Period(call.Arguments);
        var page = await call.Dispatcher.QueryAsync(
            new ListQuotationsQuery(
                call.Principal.TenantId,
                CopilotArguments.Id(call.Arguments, "clientId"),
                CopilotArguments.Id(call.Arguments, "advisorId"),
                CopilotArguments.Code(call.Arguments, "status"),
                from,
                to,
                ClientNit: null,
                CopilotArguments.Search(call.Arguments, "quotationNumber"),
                CopilotArguments.Page(call.Arguments),
                CopilotReportTools.PageSize),
            cancellationToken);
        var list = CopilotReportList<object>.Of(page.Items, page.Total, page.Page, item => new
        {
            @ref = CopilotDocumentTools.QuotationRef(item.Id, item.QuotationNumber),
            item.QuotationNumber,
            item.CreatedAt,
            client = item.ClientName,
            item.ClientId,
            advisor = item.AdvisorName,
            item.Status,
            item.Currency,
            item.Total,
            item.OrderId,
        });
        return CopilotToolResult.Ok(
            list,
            [.. page.Items.Take(CopilotReportTools.PageSize).Select(item => CopilotDocumentTools.QuotationRef(item.Id, item.QuotationNumber))]);
    }
}

internal sealed class GetQuotationTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "get_quotation",
        "El detalle de una cotización: estado, vigencia, totales, líneas con su producto y envío. No trae el PDF ni datos bancarios.",
        QuotationsPermissions.QuotationRead,
        TenantModuleKeys.Quotations,
        """
        "quotationId":{"type":"string","description":"Id de la cotización (uuid), tomado de una búsqueda."}
        """);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var quotation = await call.Dispatcher.QueryAsync(
            new GetQuotationQuery(call.Principal.TenantId, CopilotArguments.RequiredId(call.Arguments, "quotationId")), cancellationToken);
        var reference = CopilotDocumentTools.QuotationRef(quotation.Id, quotation.QuotationNumber);
        return CopilotToolResult.Ok(
            new
            {
                @ref = reference,
                quotation.QuotationNumber,
                quotation.ClientId,
                quotation.AdvisorId,
                quotation.Status,
                quotation.CreatedAt,
                quotation.SentAt,
                quotation.ValidUntil,
                quotation.PaymentMethod,
                quotation.Currency,
                quotation.Subtotal,
                quotation.TaxAmount,
                quotation.DiscountAmount,
                quotation.Total,
                quotation.RetentionAmount,
                quotation.NetTotal,
                quotation.Notes,
                quotation.CanBeConvertedToOrder,
                items = CopilotDocumentTools.Items(quotation.Items),
                shipping = CopilotDocumentTools.Shipping(quotation.Shipping),
            },
            [reference]);
    }
}
