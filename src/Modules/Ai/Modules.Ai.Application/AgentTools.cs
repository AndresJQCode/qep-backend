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
/// <param name="QuotationDraft">P-4: la asesora y la cuenta de cobro del agente (de la versión de la sesión), para
/// <c>create_quotation_draft</c>.</param>
public sealed record AgentToolInvocation(
    Guid TenantId, Guid JobId, string CallId, Guid? CustomerId, bool IsTest, AgentQuotationDraft? QuotationDraft = null);

/// <summary>Una herramienta que termina el turno con un traspaso determinista (P-4 y P-4b), sin otra vuelta del modelo.</summary>
/// <param name="Code">El código del traspaso (<see cref="AgentHandoffCodes"/>).</param>
/// <param name="MemberId">A quién va la conversación; <c>null</c> es el destino configurado del agente.</param>
public sealed record AgentToolHandoff(string Code, Guid? MemberId);

/// <param name="ResultJson">Lo que lee el modelo; con <paramref name="IsError"/>, el error (<c>{"error":"…"}</c>).</param>
/// <param name="ErrorCode">El código del error, para <c>ai.tool_calls</c>.</param>
public sealed record AgentToolResult(string ResultJson, bool IsError, string? ErrorCode)
{
    /// <summary>Si no es <c>null</c>, el bucle termina el turno con este traspaso después de anotar la llamada.</summary>
    public AgentToolHandoff? Handoff { get; init; }

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

/// <summary>§8.2: el contrato de <c>create_quotation_draft</c>, el mismo para la real y la simulada del playground.</summary>
public static class QuotationDraftArguments
{
    public const int MaxLines = 20;
    public const int MaxQuantity = 9999;
    public const int NoteMaxLength = 500;

    public static readonly AiToolDefinition Definition = new(
        AgentToolNames.CreateQuotationDraft,
        "Crea un borrador de cotización para el cliente de esta conversación con variantes del catálogo. Un asesor lo revisa antes de enviarlo.",
        """
        {"type":"object","properties":{"lines":{"type":"array","description":"De 1 a 20 líneas.","items":{"type":"object","properties":{"variantId":{"type":"string","description":"El variantId que devolvió search_catalog."},"quantity":{"type":"integer","description":"De 1 a 9999."}},"required":["variantId","quantity"],"additionalProperties":false}},"note":{"type":["string","null"],"description":"Nota para el asesor, máximo 500 caracteres."}},"required":["lines","note"],"additionalProperties":false}
        """);

    /// <summary>Valida los argumentos con el mismo rigor que un cuerpo HTTP (D-A30): de 1 a 20 líneas, ids de variante, cantidades de 1 a 9999,
    /// sin variantes repetidas (Quotations las rechazaría igual) y la nota con su tope. Una nota vacía es ninguna.</summary>
    public static bool TryRead(JsonElement arguments, out IReadOnlyList<AiQuotationDraftLine> lines, out string? note)
    {
        lines = [];
        note = null;
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("lines", out var items)
            || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() is < 1 or > MaxLines)
        {
            return false;
        }

        var read = new List<AiQuotationDraftLine>();
        foreach (var item in items.EnumerateArray())
        {
            if (!AgentToolArguments.TryString(item, "variantId", out var id)
                || !Guid.TryParse(id, out var variantId)
                || !AgentToolArguments.TryInt(item, "quantity", out var quantity)
                || quantity is < 1 or > MaxQuantity)
            {
                return false;
            }

            read.Add(new AiQuotationDraftLine(variantId, quantity));
        }

        if (read.Select(line => line.VariantId).Distinct().Count() != read.Count)
        {
            return false;
        }

        if (arguments.TryGetProperty("note", out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!.Trim();
            if (text.Length > NoteMaxLength)
            {
                return false;
            }

            note = text.Length == 0 ? null : text;
        }

        lines = read;
        return true;
    }
}

/// <summary>
/// §7 y §8.2: <c>create_quotation_draft</c> <b>sólo en el playground</b>, simulado: valida los argumentos como la real y devuelve un número
/// ficticio <c>PRUEBA-…</c> sin escribir nada. Como la real, termina el turno con el traspaso de P-4b, para que la prueba muestre lo mismo
/// que pasaría en el canal.
/// </summary>
public sealed class SimulatedQuotationDraftTool : IAgentTool
{
    public AiToolDefinition Definition => QuotationDraftArguments.Definition;

    public string Capability => AiServicePrincipal.QuotationsDraftCreate;

    public bool IsOfferedTo(AgentTools tools, bool isTest) => isTest && tools.QuotationDraft;

    public Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!QuotationDraftArguments.TryRead(arguments, out _, out _))
        {
            return Task.FromResult(AgentToolResult.Error(AgentToolErrors.InvalidArguments));
        }

        var number = $"PRUEBA-{invocation.JobId.ToString("N")[..8].ToUpperInvariant()}";
        return Task.FromResult(AgentToolResult.Ok(new { quotationNumber = number }) with
        {
            Handoff = new AgentToolHandoff(AgentHandoffCodes.QuotationDraftCreated, null),
        });
    }
}

/// <summary>Una línea del borrador: la variante del catálogo y la cantidad.</summary>
public sealed record AiQuotationDraftLine(Guid VariantId, int Quantity);

/// <summary>El borrador que el agente pide a Quotations (§8.3). El cliente es el de la conversación; la asesora y la cuenta, las del agente
/// (P-4); <see cref="ExternalKey"/>, la de D-A16.</summary>
public sealed record AiQuotationDraftRequest(
    Guid TenantId,
    Guid CustomerId,
    Guid AdvisorMemberId,
    AgentBillingAccount BillingAccount,
    IReadOnlyList<AiQuotationDraftLine> Lines,
    string? Note,
    string ExternalKey);

public enum AiQuotationDraftStatus
{
    Created,

    /// <summary>P-4: el cliente no existe o Quotations no lo deja cotizar (incompleto, sin CUC, inactivo).</summary>
    CustomerNotReady,

    /// <summary>Quotations rechazó el borrador por otra razón (una variante sin precio, la cuenta de cobro): el código vuelve al modelo.</summary>
    Rejected,
}

/// <param name="AdvisorMemberId">Con <see cref="AiQuotationDraftStatus.Created"/>: la asesora de la cotización guardada, a quien va el
/// traspaso (P-4b). Con un reintento es la de la primera llamada.</param>
/// <param name="Code">Sin <see cref="AiQuotationDraftStatus.Created"/>: el código de Quotations.</param>
public sealed record AiQuotationDraftResult(AiQuotationDraftStatus Status, string? QuotationNumber, Guid? AdvisorMemberId, string? Code)
{
    public static AiQuotationDraftResult Created(string quotationNumber, Guid advisorMemberId) =>
        new(AiQuotationDraftStatus.Created, quotationNumber, advisorMemberId, null);

    public static AiQuotationDraftResult CustomerNotReady(string code) => new(AiQuotationDraftStatus.CustomerNotReady, null, null, code);

    public static AiQuotationDraftResult Rejected(string code) => new(AiQuotationDraftStatus.Rejected, null, null, code);
}

/// <summary>§8.3: el borrador de cotización sin sesión (adaptador en Bootstrapper sobre <c>IExternalQuotationWriter</c>). Idempotente por
/// <see cref="AiQuotationDraftRequest.ExternalKey"/>.</summary>
public interface IAiQuotationDrafts
{
    Task<AiQuotationDraftResult> CreateAsync(AiQuotationDraftRequest request, CancellationToken cancellationToken);
}

/// <summary>P-4: quién puede quedar como asesor de un borrador: miembros activos con <c>quotations.quotation.manage</c> (adaptador en
/// Bootstrapper).</summary>
public interface IAiQuotationAdvisors
{
    Task<IReadOnlyList<AiMemberRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<bool> CanQuoteAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken);
}

/// <summary>
/// §8.2, P-4 y P-4b: <c>create_quotation_draft</c> en un canal real. Escribe un borrador en Quotations con el cliente de la conversación (nunca
/// uno de un argumento), la asesora y la cuenta del agente y la clave <c>ai:{jobId}:{callId}</c> (D-A16), y termina el turno con un
/// traspaso: a la asesora de la cotización si la creó, al destino configurado si el cliente no se puede cotizar. Una asesora que ya no puede
/// cotizar bloquea la herramienta: el modelo lo lee como error y sigue.
/// </summary>
public sealed class CreateQuotationDraftTool(IAiQuotationDrafts drafts, IAiQuotationAdvisors advisors) : IAgentTool
{
    /// <summary><c>Quotation.ExternalKeyMaxLength</c>: <c>ai:</c>, el id del trabajo, <c>:</c> y lo que quepa del id de la llamada.</summary>
    internal const int ExternalKeyMaxLength = 128;

    public AiToolDefinition Definition => QuotationDraftArguments.Definition;

    public string Capability => AiServicePrincipal.QuotationsDraftCreate;

    public bool IsOfferedTo(AgentTools tools, bool isTest) => !isTest && tools.QuotationDraft;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolInvocation invocation, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!QuotationDraftArguments.TryRead(arguments, out var lines, out var note))
        {
            return AgentToolResult.Error(AgentToolErrors.InvalidArguments);
        }

        if (invocation.CustomerId is not { } customerId)
        {
            return AgentToolResult.Error(AgentToolErrors.CustomerUnknown) with { Handoff = CustomerNotReady };
        }

        if (invocation.QuotationDraft is not { AdvisorMemberId: { } advisorId, BillingAccount: { } account })
        {
            return AgentToolResult.Error(AgentToolErrors.QuotationNotConfigured);
        }

        if (!await advisors.CanQuoteAsync(invocation.TenantId, advisorId, cancellationToken))
        {
            return AgentToolResult.Error(AgentToolErrors.QuotationAdvisorUnavailable);
        }

        var result = await drafts.CreateAsync(
            new AiQuotationDraftRequest(invocation.TenantId, customerId, advisorId, account, lines, note, ExternalKey(invocation)),
            cancellationToken);
        return result.Status switch
        {
            AiQuotationDraftStatus.Created => AgentToolResult.Ok(new { quotationNumber = result.QuotationNumber }) with
            {
                Handoff = new AgentToolHandoff(AgentHandoffCodes.QuotationDraftCreated, result.AdvisorMemberId),
            },
            AiQuotationDraftStatus.CustomerNotReady =>
                AgentToolResult.Error(result.Code ?? AgentToolErrors.CustomerUnknown) with { Handoff = CustomerNotReady },
            _ => AgentToolResult.Error(result.Code ?? AgentToolErrors.ToolFailed),
        };
    }

    private static AgentToolHandoff CustomerNotReady => new(AgentHandoffCodes.QuotationCustomerNotReady, null);

    /// <summary>D-A16: el mismo trabajo y la misma llamada dan la misma clave, así un trabajo retomado no crea otro borrador.</summary>
    internal static string ExternalKey(AgentToolInvocation invocation)
    {
        var prefix = $"ai:{invocation.JobId:D}:";
        var room = ExternalKeyMaxLength - prefix.Length;
        return prefix + (invocation.CallId.Length <= room ? invocation.CallId : invocation.CallId[..room]);
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

    /// <summary>P-4: la asesora del agente ya no puede cotizar; la herramienta queda bloqueada hasta que se elija otra.</summary>
    public const string QuotationAdvisorUnavailable = "quotation_advisor_unavailable";

    /// <summary>P-4: el agente no tiene asesora o cuenta de cobro (publicar ya lo exige; queda por una versión vieja).</summary>
    public const string QuotationNotConfigured = "quotation_not_configured";
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
