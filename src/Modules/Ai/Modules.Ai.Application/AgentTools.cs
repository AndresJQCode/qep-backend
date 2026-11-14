using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>Spec 2026-10-29 §8.2: los nombres que ve el modelo. Son contrato con los prompts y con la actividad del agente.</summary>
public static class AgentToolNames
{
    public const string SearchCatalog = "search_catalog";
    public const string GetOrderStatus = "get_order_status";
    public const string CreateQuotationDraft = "create_quotation_draft";

    /// <summary>Herramienta final: la respuesta al cliente.</summary>
    public const string SendReply = "send_reply";

    /// <summary>Herramienta final: el traspaso a una persona.</summary>
    public const string HandoffToHuman = "handoff_to_human";
}

/// <summary>
/// Spec 2026-10-29 §8.1: el principal de servicio del agente es un conjunto fijo de capacidades, sin roles ni permisos editables. Una
/// herramienta se ofrece sólo si su capacidad está aquí <b>y</b> el agente la tiene activa en <c>config.tools</c>.
/// </summary>
public static class AiServicePrincipal
{
    public const string CatalogRead = "catalog.read";
    public const string OrdersReadOwn = "orders.read_own";
    public const string QuotationsDraftCreate = "quotations.draft_create";

    public static readonly IReadOnlySet<string> Capabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        CatalogRead,
        OrdersReadOwn,
        QuotationsDraftCreate,
    };
}

/// <summary>Una variante activa del catálogo como la ve el modelo (§8.2). Sin existencias (P-3, fuera del MVP).</summary>
/// <param name="Price">El precio de lista con IVA en la moneda por defecto del tenant; <c>null</c> si la variante no tiene precio en
/// esa moneda.</param>
public sealed record AiCatalogItem(Guid VariantId, string Name, string Code, decimal? Price, string Currency, bool TaxIncluded, bool Active);

/// <summary>§8.3: la búsqueda en el catálogo del tenant, sin sesión (adaptador en Bootstrapper). Sólo variantes activas.</summary>
public interface IAiCatalogSearch
{
    Task<IReadOnlyList<AiCatalogItem>> SearchAsync(Guid tenantId, string query, int limit, CancellationToken cancellationToken);
}

/// <summary>Un pedido del cliente de la conversación como lo ve el modelo (§8.2). Los estados viajan con el nombre del enum.</summary>
public sealed record AiOrderStatus(
    string OrderNumber, string Status, string PaymentStatus, DateTimeOffset ConvertedAt, decimal Total, string Currency);

/// <summary>§8.3: los pedidos de un cliente, sin sesión (adaptador en Bootstrapper). Siempre del cliente que recibe, nunca de otro: un
/// número de otro cliente responde igual que uno que no existe.</summary>
public interface IAiOrderStatusReader
{
    Task<IReadOnlyList<AiOrderStatus>> ListAsync(
        Guid tenantId, Guid customerId, string? orderNumber, int limit, CancellationToken cancellationToken);
}

/// <summary>§8.3: las respuestas rápidas del tenant, como fuente del agente (adaptador sobre Mensajería en Bootstrapper).</summary>
public interface IAiQuickReplies
{
    Task<IReadOnlyList<AiQuickReply>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Con qué corre una herramienta: el tenant y el cliente salen siempre de la sesión o de la prueba, nunca de un argumento del modelo
/// (D-A30).</summary>
public sealed record AgentToolInvocation(Guid TenantId, Guid JobId, string CallId, Guid? CustomerId, bool IsTest);

/// <param name="ResultJson">Lo que lee el modelo; con <paramref name="IsError"/>, el error (<c>{"error":"…"}</c>).</param>
/// <param name="ErrorCode">El código del error, para <c>ai.tool_calls</c>.</param>
public sealed record AgentToolResult(string ResultJson, bool IsError, string? ErrorCode)
{
    public static AgentToolResult Ok(object value) => new(JsonSerializer.Serialize(value, AgentToolJson.Options), false, null);

    public static AgentToolResult Error(string code) =>
        new(JsonSerializer.Serialize(new { error = code }, AgentToolJson.Options), true, code);
}

internal static class AgentToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>Una herramienta de QEP que el agente puede llamar a mitad de su turno (las finales las resuelve el bucle).</summary>
public interface IAgentTool
{
    AiToolDefinition Definition { get; }

    /// <summary>La capacidad del principal de servicio que necesita (§8.1).</summary>
    string Capability { get; }

    /// <summary>Si el agente la tiene activa en su <c>config.tools</c>, y si existe en este modo (real o prueba).</summary>
    bool IsOfferedTo(AgentTools tools, bool isTest);

    Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>§8.2: <c>search_catalog</c>. <c>query</c> ≤ 120, <c>limit</c> 1..10; validados aquí con el mismo rigor que un cuerpo HTTP.</summary>
public sealed class SearchCatalogTool(IAiCatalogSearch catalog) : IAgentTool
{
    public const int QueryMaxLength = 120;
    public const int MaxLimit = 10;

    public AiToolDefinition Definition { get; } = new(
        AgentToolNames.SearchCatalog,
        "Busca productos activos del catálogo del negocio por nombre o código. Devuelve el nombre, el código y el precio de lista con IVA.",
        """
        {"type":"object","properties":{"query":{"type":"string","description":"Lo que busca el cliente: nombre o código del producto, máximo 120 caracteres."},"limit":{"type":"integer","description":"Cuántos resultados devolver, de 1 a 10."}},"required":["query","limit"],"additionalProperties":false}
        """);

    public string Capability => AiServicePrincipal.CatalogRead;

    public bool IsOfferedTo(AgentTools tools, bool isTest) => tools.Catalog;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!AgentToolArguments.TryString(arguments, "query", out var query)
            || query.Trim().Length is 0 or > QueryMaxLength
            || !AgentToolArguments.TryInt(arguments, "limit", out var limit)
            || limit is < 1 or > MaxLimit)
        {
            return AgentToolResult.Error(AgentToolErrors.InvalidArguments);
        }

        var items = await catalog.SearchAsync(invocation.TenantId, query.Trim(), limit, cancellationToken);
        return AgentToolResult.Ok(items.Take(limit).Select(item => new
        {
            variantId = item.VariantId,
            name = item.Name,
            code = item.Code,
            price = item.Price,
            currency = item.Currency,
            taxIncluded = item.TaxIncluded,
            active = item.Active,
        }));
    }
}

/// <summary>§8.2: <c>get_order_status</c>. Sólo los pedidos del cliente de la conversación, los 5 más recientes; sin cliente,
/// <c>customer_unknown</c>.</summary>
public sealed class GetOrderStatusTool(IAiOrderStatusReader orders) : IAgentTool
{
    public const int OrderNumberMaxLength = 40;
    public const int MaxOrders = 5;

    public AiToolDefinition Definition { get; } = new(
        AgentToolNames.GetOrderStatus,
        "Consulta el estado de los pedidos del cliente de esta conversación. Sólo ve sus propios pedidos.",
        """
        {"type":"object","properties":{"orderNumber":{"type":["string","null"],"description":"El número de pedido si el cliente lo dio, máximo 40 caracteres; null para ver sus pedidos más recientes."}},"required":["orderNumber"],"additionalProperties":false}
        """);

    public string Capability => AiServicePrincipal.OrdersReadOwn;

    public bool IsOfferedTo(AgentTools tools, bool isTest) => tools.OrderStatus;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        string? orderNumber = null;
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("orderNumber", out var value)
            || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            return AgentToolResult.Error(AgentToolErrors.InvalidArguments);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            orderNumber = value.GetString()!.Trim();
            if (orderNumber.Length > OrderNumberMaxLength)
            {
                return AgentToolResult.Error(AgentToolErrors.InvalidArguments);
            }

            orderNumber = orderNumber.Length == 0 ? null : orderNumber;
        }

        if (invocation.CustomerId is not { } customerId)
        {
            return AgentToolResult.Error(AgentToolErrors.CustomerUnknown);
        }

        var found = await orders.ListAsync(invocation.TenantId, customerId, orderNumber, MaxOrders, cancellationToken);
        return AgentToolResult.Ok(found.Take(MaxOrders).Select(order => new
        {
            orderNumber = order.OrderNumber,
            status = order.Status,
            paymentStatus = order.PaymentStatus,
            convertedAt = order.ConvertedAt,
            total = order.Total,
            currency = order.Currency,
        }));
    }
}

/// <summary>
/// §7 y §8.2: <c>create_quotation_draft</c> <b>sólo en el playground</b>, simulado: valida los argumentos como la real y devuelve un número
/// ficticio <c>PRUEBA-…</c> sin escribir nada. La herramienta real (escritura con clave externa, P-4) llega con la Tarea 8; hasta entonces
/// un agente en un canal real no la ve.
/// </summary>
public sealed class SimulatedQuotationDraftTool : IAgentTool
{
    public const int MaxLines = 20;
    public const int MaxQuantity = 9999;
    public const int NoteMaxLength = 500;

    public AiToolDefinition Definition { get; } = new(
        AgentToolNames.CreateQuotationDraft,
        "Crea un borrador de cotización para el cliente de esta conversación con variantes del catálogo. Un asesor lo revisa antes de enviarlo.",
        """
        {"type":"object","properties":{"lines":{"type":"array","description":"De 1 a 20 líneas.","items":{"type":"object","properties":{"variantId":{"type":"string","description":"El variantId que devolvió search_catalog."},"quantity":{"type":"integer","description":"De 1 a 9999."}},"required":["variantId","quantity"],"additionalProperties":false}},"note":{"type":["string","null"],"description":"Nota para el asesor, máximo 500 caracteres."}},"required":["lines","note"],"additionalProperties":false}
        """);

    public string Capability => AiServicePrincipal.QuotationsDraftCreate;

    public bool IsOfferedTo(AgentTools tools, bool isTest) => isTest && tools.QuotationDraft;

    public Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("lines", out var lines)
            || lines.ValueKind != JsonValueKind.Array
            || lines.GetArrayLength() is < 1 or > MaxLines
            || lines.EnumerateArray().Any(line => !AgentToolArguments.TryString(line, "variantId", out var id)
                || !Guid.TryParse(id, out _)
                || !AgentToolArguments.TryInt(line, "quantity", out var quantity)
                || quantity is < 1 or > MaxQuantity)
            || (arguments.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String
                && note.GetString()!.Length > NoteMaxLength))
        {
            return Task.FromResult(AgentToolResult.Error(AgentToolErrors.InvalidArguments));
        }

        var number = $"PRUEBA-{invocation.JobId.ToString("N")[..8].ToUpperInvariant()}";
        return Task.FromResult(AgentToolResult.Ok(new { quotationNumber = number }));
    }
}

/// <summary>Los códigos de error que lee el modelo en un resultado de herramienta (y que guarda <c>ai.tool_calls.error_code</c>).</summary>
public static class AgentToolErrors
{
    public const string InvalidArguments = "invalid_arguments";

    /// <summary>§8.2: la conversación no tiene cliente (o la prueba no fijó uno).</summary>
    public const string CustomerUnknown = "customer_unknown";

    /// <summary>La herramienta no está entre las que el agente tiene activas.</summary>
    public const string UnknownTool = "unknown_tool";

    /// <summary>La herramienta falló por dentro; el detalle va al log, no al modelo.</summary>
    public const string ToolFailed = "tool_failed";
}

internal static class AgentToolArguments
{
    public static bool TryString(JsonElement element, string name, [NotNullWhen(true)] out string? value)
    {
        value = element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        return value is not null;
    }

    public static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }
}
