using System.Globalization;
using System.Text.Json;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Reporting.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.Copilot;

/// <summary>
/// Spec 2026-11-17 (copiloto) §6 y plan fase 2 C2.5: las herramientas de reportes. Cada una arma <b>la misma consulta</b> que su endpoint de
/// <c>ReportingEndpoints</c> con los filtros del modelo y el tenant del hilo, y la despacha en el scope delegado: el handler autoriza al
/// miembro y le acota el asesor (<c>ReportingAuthorization.ScopeAdvisorAsync</c>) como si hubiera llamado él. D-C5: los totales salen sólo de
/// los resúmenes; las listas no pasan de <see cref="PageSize"/> filas y dicen cuántas hay.
/// </summary>
internal static class CopilotReportTools
{
    /// <summary>D-C5: filas por página de una lista. Pocas a propósito: una lista no es para sumar.</summary>
    public const int PageSize = 20;

    public static IReadOnlyList<ICopilotTool> All() =>
    [
        new GetOrdersSummaryTool(),
        new ListOrdersReportTool(),
        new GetQuotationsSummaryTool(),
        new ListQuotationsReportTool(),
        new GetCustomersSummaryTool(),
        new GetMarginSummaryTool(),
    ];

    /// <summary>El alcance real de un reporte de pedidos o cotizaciones: sin <c>reporting.all_advisors.read</c> el handler lo acotó al miembro.
    /// Se lo dice al modelo para que no presente lo propio como el total de la empresa.</summary>
    public static string ScopeOf(CopilotToolCall call) =>
        call.Permissions.Contains(ReportingPermissions.AllAdvisorsRead, StringComparer.Ordinal) ? "all" : "own";

    /// <param name="properties">Los fragmentos de <c>properties</c> del esquema (<c>"nombre":{…}</c>), en orden.</param>
    public static CopilotToolContract Contract(string name, string description, string permission, params string[] properties) =>
        Contract(name, description, permission, TenantModuleKeys.Reporting, properties);

    /// <param name="module">El módulo del endpoint que la herramienta replica: el mismo que enmascara su permiso.</param>
    public static CopilotToolContract Contract(
        string name, string description, string permission, TenantModuleKey module, params string[] properties) =>
        new(new AiToolDefinition(name, description, Schema("{" + string.Join(',', properties) + "}")), permission, module);

    /// <summary>C-8: estricto. Todas las propiedades en <c>required</c> (las opcionales aceptan <c>null</c>) y sin propiedades extra.</summary>
    private static string Schema(string propertiesJson)
    {
        using var properties = JsonDocument.Parse(propertiesJson);
        var required = string.Join(',', properties.RootElement.EnumerateObject().Select(property => $"\"{property.Name}\""));
        return "{\"type\":\"object\",\"properties\":" + properties.RootElement.GetRawText() + ",\"required\":[" + required
            + "],\"additionalProperties\":false}";
    }

    public const string Period =
        """
        "from":{"type":["string","null"],"description":"Primer día del período, yyyy-MM-dd, en la fecha del negocio. null: sin límite."},
        "to":{"type":["string","null"],"description":"Último día del período, yyyy-MM-dd, inclusive. Hasta 400 días después de from."}
        """;

    public const string Page =
        """
        "page":{"type":["integer","null"],"description":"Página de 20 filas, desde 1. null: la primera."}
        """;
}

/// <summary>Los argumentos del modelo, validados con el rigor de un request (§6). Un valor que no sirve sale como
/// <see cref="CopilotArgumentException"/> y vuelve al modelo como <c>invalid_arguments</c>.</summary>
internal static class CopilotArguments
{
    /// <summary>§6: un rango de fechas de a lo sumo 400 días.</summary>
    public const int MaxRangeDays = 400;

    public const int MaxPage = 50;

    public static (DateOnly? From, DateOnly? To) Period(JsonElement arguments)
    {
        var from = Date(arguments, "from");
        var to = Date(arguments, "to");
        if (from is { } start && to is { } end && (end < start || end.DayNumber - start.DayNumber > MaxRangeDays))
        {
            throw new CopilotArgumentException("to");
        }

        return (from, to);
    }

    public static DateOnly? Date(JsonElement arguments, string name) =>
        Value(arguments, name) switch
        {
            null => null,
            { ValueKind: JsonValueKind.String } value when DateOnly.TryParseExact(
                value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) => date,
            _ => throw new CopilotArgumentException(name),
        };

    public static Guid? Id(JsonElement arguments, string name) =>
        Value(arguments, name) switch
        {
            null => null,
            { ValueKind: JsonValueKind.String } value when Guid.TryParseExact(value.GetString(), "D", out var id) => id,
            _ => throw new CopilotArgumentException(name),
        };

    /// <summary>Un texto corto (un estado, un origen, una moneda): el validador del handler decide si es uno de los suyos.</summary>
    public static string? Code(JsonElement arguments, string name) =>
        Value(arguments, name) switch
        {
            null => null,
            { ValueKind: JsonValueKind.String } value when value.GetString()!.Trim() is { Length: > 0 and <= 40 } text => text,
            _ => throw new CopilotArgumentException(name),
        };

    public static bool? Flag(JsonElement arguments, string name) =>
        Value(arguments, name) switch
        {
            null => null,
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => throw new CopilotArgumentException(name),
        };

    /// <summary>El id de un detalle: sin él no hay qué leer.</summary>
    public static Guid RequiredId(JsonElement arguments, string name) => Id(arguments, name) ?? throw new CopilotArgumentException(name);

    /// <summary>Un texto de búsqueda corto (un número de documento, un nombre). El handler lo usa como filtro, nunca como instrucción.</summary>
    public static string? Search(JsonElement arguments, string name) =>
        Value(arguments, name) switch
        {
            null => null,
            { ValueKind: JsonValueKind.String } value when value.GetString()!.Trim() is { Length: <= 100 } text => text.Length == 0 ? null : text,
            _ => throw new CopilotArgumentException(name),
        };

    public static int Page(JsonElement arguments) =>
        Value(arguments, "page") switch
        {
            null => 1,
            { ValueKind: JsonValueKind.Number } value when value.TryGetInt32(out var page) && page is >= 1 and <= MaxPage => page,
            _ => throw new CopilotArgumentException("page"),
        };

    /// <summary><c>null</c> si falta o vale <c>null</c>.</summary>
    private static JsonElement? Value(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;
}

/// <summary>Una lista de reporte como la lee el modelo: a lo sumo 20 filas, el total real y si quedó algo afuera.</summary>
internal sealed record CopilotReportList<TItem>(IReadOnlyList<TItem> Items, int Total, int Page, bool Truncated)
{
    public static CopilotReportList<TItem> Of<TSource>(ReportPage<TSource> page, Func<TSource, TItem> map) =>
        Of(page.Items, page.Total, page.Page, map);

    /// <summary>Lo mismo para las páginas de los otros módulos, que traen sus filas, el total y la página con otros nombres.</summary>
    public static CopilotReportList<TItem> Of<TSource>(IEnumerable<TSource> items, int total, int page, Func<TSource, TItem> map) =>
        new([.. items.Take(CopilotReportTools.PageSize).Select(map)], total, page, page * CopilotReportTools.PageSize < total);
}

internal sealed class GetOrdersSummaryTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "get_orders_summary",
        "Resumen de ventas (pedidos) del período: cantidad y totales por moneda, por mes, por asesor, por cliente y por origen, con la comparación contra el período anterior. Es la única forma de obtener totales de ventas. scope dice si el resumen es de toda la empresa (all) o sólo de quien pregunta (own).",
        ReportingPermissions.OrdersRead,
        CopilotReportTools.Period,
        """
        "advisorId":{"type":["string","null"],"description":"Id del asesor (uuid) o null para todos."},"clientId":{"type":["string","null"],"description":"Id del cliente (uuid) o null."},"paymentStatus":{"type":["string","null"],"description":"FullPaymentReceived, PartialPaymentReceived, PaymentPending o null."},"source":{"type":["string","null"],"description":"Quotation, Manual, Shopify, Pos, Store o null."}
        """);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var (from, to) = CopilotArguments.Period(call.Arguments);
        var summary = await call.Dispatcher.QueryAsync(
            new GetOrdersReportSummaryQuery(new OrdersReportFilter(
                call.Principal.TenantId, from, to, CopilotArguments.Id(call.Arguments, "advisorId"), CopilotArguments.Id(call.Arguments, "clientId"),
                CopilotArguments.Code(call.Arguments, "paymentStatus"), CopilotArguments.Code(call.Arguments, "source"))),
            cancellationToken);
        return CopilotToolResult.Ok(
            new { scope = CopilotReportTools.ScopeOf(call), summary },
            [
                .. summary.ByClient
                    .Where(entry => entry.Id is not null)
                    .Select(entry => new CopilotCitation(CopilotCitationKinds.Customer, entry.Id!.Value.ToString(), entry.Label ?? entry.Id.Value.ToString())),
            ]);
    }
}

internal sealed class ListOrdersReportTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "list_orders_report",
        "Lista de pedidos del período, 20 por página, con su número, cliente, asesor, estado, estado de pago y total. Sirve para encontrar pedidos concretos; nunca sumes sus filas: los totales salen de get_orders_summary.",
        ReportingPermissions.OrdersRead,
        CopilotReportTools.Period,
        """
        "advisorId":{"type":["string","null"],"description":"Id del asesor (uuid) o null."},"clientId":{"type":["string","null"],"description":"Id del cliente (uuid) o null."},"paymentStatus":{"type":["string","null"],"description":"FullPaymentReceived, PartialPaymentReceived, PaymentPending o null."},"source":{"type":["string","null"],"description":"Quotation, Manual, Shopify, Pos, Store o null."}
        """,
        CopilotReportTools.Page);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var (from, to) = CopilotArguments.Period(call.Arguments);
        var page = await call.Dispatcher.QueryAsync(
            new ListOrdersReportQuery(
                new OrdersReportFilter(
                    call.Principal.TenantId, from, to, CopilotArguments.Id(call.Arguments, "advisorId"), CopilotArguments.Id(call.Arguments, "clientId"),
                    CopilotArguments.Code(call.Arguments, "paymentStatus"), CopilotArguments.Code(call.Arguments, "source")),
                CopilotArguments.Page(call.Arguments),
                CopilotReportTools.PageSize),
            cancellationToken);
        var list = CopilotReportList<object>.Of(page, item => new
        {
            @ref = Ref(item),
            item.OrderNumber,
            item.Source,
            item.QuotationNumber,
            item.ConvertedAt,
            advisor = item.AdvisorName,
            client = item.ClientName,
            item.Status,
            item.PaymentStatus,
            item.Currency,
            item.Total,
        });
        return CopilotToolResult.Ok(list, [.. page.Items.Take(CopilotReportTools.PageSize).Select(Ref)]);
    }

    private static CopilotCitation Ref(OrdersReportItemDto item) => new(CopilotCitationKinds.Order, item.OrderId.ToString(), item.OrderNumber);
}

internal sealed class GetQuotationsSummaryTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "get_quotations_summary",
        "Resumen de cotizaciones del período: cantidad y totales por moneda, por estado, por mes y por asesor, vigencia y las que están por vencer. Es la única forma de obtener totales de cotizaciones. scope dice si es de toda la empresa (all) o sólo de quien pregunta (own).",
        ReportingPermissions.QuotationRead,
        CopilotReportTools.Period,
        """
        "advisorId":{"type":["string","null"],"description":"Id del asesor (uuid) o null."},"clientId":{"type":["string","null"],"description":"Id del cliente (uuid) o null."},"status":{"type":["string","null"],"description":"Draft, Sent, Expired, Voided, Converted o null."}
        """);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var (from, to) = CopilotArguments.Period(call.Arguments);
        var summary = await call.Dispatcher.QueryAsync(
            new GetQuotationsReportSummaryQuery(new QuotationsReportFilter(
                call.Principal.TenantId, from, to, CopilotArguments.Id(call.Arguments, "advisorId"), CopilotArguments.Id(call.Arguments, "clientId"),
                CopilotArguments.Code(call.Arguments, "status"))),
            cancellationToken);
        return CopilotToolResult.Ok(
            new { scope = CopilotReportTools.ScopeOf(call), summary },
            [.. summary.Expiring.Select(entry => new CopilotCitation(CopilotCitationKinds.Quotation, entry.QuotationId.ToString(), entry.QuotationNumber))]);
    }
}

internal sealed class ListQuotationsReportTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "list_quotations_report",
        "Lista de cotizaciones del período, 20 por página, con su número, cliente, asesor, estado, vigencia y total. Sirve para encontrar cotizaciones concretas; nunca sumes sus filas: los totales salen de get_quotations_summary.",
        ReportingPermissions.QuotationRead,
        CopilotReportTools.Period,
        """
        "advisorId":{"type":["string","null"],"description":"Id del asesor (uuid) o null."},"clientId":{"type":["string","null"],"description":"Id del cliente (uuid) o null."},"status":{"type":["string","null"],"description":"Draft, Sent, Expired, Voided, Converted o null."}
        """,
        CopilotReportTools.Page);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var (from, to) = CopilotArguments.Period(call.Arguments);
        var page = await call.Dispatcher.QueryAsync(
            new ListQuotationsReportQuery(
                new QuotationsReportFilter(
                    call.Principal.TenantId, from, to, CopilotArguments.Id(call.Arguments, "advisorId"), CopilotArguments.Id(call.Arguments, "clientId"),
                    CopilotArguments.Code(call.Arguments, "status")),
                CopilotArguments.Page(call.Arguments),
                CopilotReportTools.PageSize),
            cancellationToken);
        var list = CopilotReportList<object>.Of(page, item => new
        {
            @ref = Ref(item),
            item.QuotationNumber,
            item.CreatedAt,
            item.ValidUntil,
            advisor = item.AdvisorName,
            client = item.ClientName,
            item.Status,
            item.Currency,
            item.Total,
        });
        return CopilotToolResult.Ok(list, [.. page.Items.Take(CopilotReportTools.PageSize).Select(Ref)]);
    }

    private static CopilotCitation Ref(QuotationsReportItemDto item) =>
        new(CopilotCitationKinds.Quotation, item.QuotationId.ToString(), item.QuotationNumber);
}

internal sealed class GetCustomersSummaryTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "get_customers_summary",
        "Resumen de la cartera de clientes: cuántos hay, cuántos activos, altas por mes y reparto por clasificación y departamento, con la comparación contra el período anterior. from y to cortan por fecha de alta.",
        ReportingPermissions.CustomerRead,
        CopilotReportTools.Period,
        """
        "isActive":{"type":["boolean","null"],"description":"true sólo activos, false sólo inactivos, null todos."},"classificationId":{"type":["string","null"],"description":"Id de la clasificación (uuid) o null."},"departmentId":{"type":["string","null"],"description":"Id del departamento (uuid) o null."}
        """);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var (from, to) = CopilotArguments.Period(call.Arguments);
        var summary = await call.Dispatcher.QueryAsync(
            new GetCustomerReportSummaryQuery(new CustomerReportFilter(
                call.Principal.TenantId, from, to, CopilotArguments.Flag(call.Arguments, "isActive"),
                CopilotArguments.Id(call.Arguments, "classificationId"), CopilotArguments.Id(call.Arguments, "departmentId"))),
            cancellationToken);
        return CopilotToolResult.Ok(new { summary }, []);
    }
}

internal sealed class GetMarginSummaryTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "get_margin_summary",
        "Resumen del margen bruto del período por moneda: ventas netas, costo, utilidad y porcentaje de margen, sólo de las líneas con costo, y lo que quedó fuera por no tener costo.",
        ReportingPermissions.MarginRead,
        CopilotReportTools.Period,
        """
        "source":{"type":["string","null"],"description":"Quotation, Manual, Shopify, Pos, Store o null."}
        """);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var (from, to) = CopilotArguments.Period(call.Arguments);
        var summary = await call.Dispatcher.QueryAsync(
            new GetMarginReportSummaryQuery(new MarginReportFilter(call.Principal.TenantId, from, to, CopilotArguments.Code(call.Arguments, "source"))),
            cancellationToken);
        return CopilotToolResult.Ok(new { summary }, []);
    }
}

internal sealed class GetPriceChangesSummaryTool : ICopilotTool
{
    public CopilotToolContract Contract { get; } = CopilotReportTools.Contract(
        "get_price_changes_summary",
        "Resumen de los cambios de precio del catálogo en el período: cuántos, subidas y bajadas, por mes, por campo y por producto.",
        ReportingPermissions.PriceChangeRead,
        CopilotReportTools.Period,
        """
        "productId":{"type":["string","null"],"description":"Id del producto (uuid) o null."},"variantId":{"type":["string","null"],"description":"Id de la variante (uuid) o null."},"field":{"type":["string","null"],"description":"PriceBase, ScaleDiscount o null."},"currency":{"type":["string","null"],"description":"Código ISO de la moneda (COP, USD…) o null."}
        """);

    public async Task<CopilotToolResult> ExecuteAsync(CopilotToolCall call, CancellationToken cancellationToken)
    {
        var (from, to) = CopilotArguments.Period(call.Arguments);
        var summary = await call.Dispatcher.QueryAsync(
            new GetPriceChangeReportSummaryQuery(new PriceChangeReportFilter(
                call.Principal.TenantId, from, to, CopilotArguments.Id(call.Arguments, "productId"), ChangedBy: null,
                CopilotArguments.Code(call.Arguments, "field"), CopilotArguments.Code(call.Arguments, "currency"),
                CopilotArguments.Id(call.Arguments, "variantId"))),
            cancellationToken);
        return CopilotToolResult.Ok(new { summary }, []);
    }
}
