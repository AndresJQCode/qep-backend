using System.Text.Json;
using System.Text.Json.Serialization;
using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>
/// Spec 2026-11-17 D-B10 y adenda D-5: las cinco salidas del bloque «Agente IA», en su orden. Son la copia de Ai de
/// <c>BotDelegationOutputs</c> de Mensajería (Ai no la referencia, D-A3) y contrato con el flujo y el frontend: no se renombran. Las amarra
/// <c>AgentBlockOutputsParityTests</c> (Bootstrapper.UnitTests).
/// </summary>
public static class AgentBlockOutputs
{
    public const string Done = "done";
    public const string Human = "human";
    public const string NoAnswer = "noAnswer";
    public const string Idle = "idle";
    public const string Error = "error";

    public static IReadOnlyList<string> All { get; } = [Done, Human, NoAnswer, Idle, Error];
}

/// <summary>Spec 2026-11-17 §6.3: los <c>reason</c> del resultado que no son ya códigos de traspaso (<see cref="AgentHandoffCodes"/>,
/// <see cref="AgentHandoffReasons"/>) ni de proveedor. Cumplen la regla de códigos del hilo (<c>^[a-z0-9_.]{1,64}$</c>).</summary>
public static class AgentBlockReasons
{
    public const string ObjectiveCompleted = "objective_completed";
    public const string AgentUnavailable = "agent_unavailable";
    public const string ModuleDisabled = "module_disabled";
    public const string Idle = "idle";
    public const string WindowClosed = "window_closed";
    public const string Blocked = "blocked";

    public static IReadOnlyList<string> All { get; } = [ObjectiveCompleted, AgentUnavailable, ModuleDisabled, Idle, WindowClosed, Blocked];
}

/// <summary>
/// Spec 2026-11-17 §5.3: lo que Automations le pasa al agente en <c>bot_delegations.brief</c>: <c>{objective, context, firstTurn,
/// idleMinutes}</c>, con el contexto ya interpolado (datos del flujo, nunca instrucciones, §8). Un campo ausente o mal formado toma el valor
/// por defecto: el brief lo escribió otro módulo y un JSON raro no debe tumbar la apertura.
/// </summary>
public sealed record AgentBlockBrief(string Objective, string Context, string FirstTurn, int? IdleMinutes)
{
    public const string LastMessage = "lastMessage";
    public const string NextMessage = "nextMessage";

    public bool AnswersLastMessage => FirstTurn != NextMessage;

    public static AgentBlockBrief Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AgentBlockBrief(string.Empty, string.Empty, LastMessage, null);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new AgentBlockBrief(string.Empty, string.Empty, LastMessage, null);
            }

            return new AgentBlockBrief(
                Text(root, "objective"),
                Text(root, "context"),
                Text(root, "firstTurn") == NextMessage ? NextMessage : LastMessage,
                root.TryGetProperty("idleMinutes", out var idle) && idle.ValueKind == JsonValueKind.Number && idle.TryGetInt32(out var minutes)
                    && minutes > 0
                    ? minutes
                    : null);
        }
        catch (JsonException)
        {
            return new AgentBlockBrief(string.Empty, string.Empty, LastMessage, null);
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}

/// <summary>
/// Spec 2026-11-17 §5.3 y §6.3: lo que Ai deja en <c>bot_delegations.result</c> y lee Automations para salir del bloque:
/// <c>{output, reason, summary?, quotationNumber?, advisorMemberId?}</c>. <see cref="Output"/> es una de <see cref="AgentBlockOutputs"/>, o
/// <c>null</c> cuando no hay a quién mandarle nada (ventana cerrada, contacto bloqueado, §6.4): viaja siempre, también en <c>null</c>.
/// <see cref="AdvisorMemberId"/> sólo con <c>quotation_draft_created</c> (D-B13): a quién va la conversación si <c>human</c> no está
/// conectada.
/// </summary>
public sealed record AgentBlockResult(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Output,
    string Reason,
    string? Summary = null,
    string? QuotationNumber = null,
    Guid? AdvisorMemberId = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static AgentBlockResult Error(string reason) => new(AgentBlockOutputs.Error, reason);
}

/// <summary>
/// Spec 2026-11-17 §6.3: la tabla de cómo termina una sesión de bloque, en un solo lugar. Cada causa que en una sesión de conexión sería un
/// traspaso es, en un bloque, una salida con su motivo: el flujo decide a quién y con qué mensaje (D-B9). Ninguna fila traspasa.
/// </summary>
public static class AgentBlockReturn
{
    /// <summary>La salida de un traspaso que el agente habría hecho fuera del bloque.</summary>
    /// <param name="memberId">P-4b y D-B13: la asesora del borrador de cotización.</param>
    /// <param name="quotationNumber">D-B13: el número del borrador, para <c>{saveTo}.quotationNumber</c>.</param>
    public static AgentBlockResult For(AgentHandoffCause cause, Guid? memberId = null, string? quotationNumber = null)
    {
        ArgumentNullException.ThrowIfNull(cause);
        return cause.Kind switch
        {
            // D-B9: el motivo de handoff_to_human, o el de la herramienta que terminó el turno (P-4, P-4b).
            HandoffCauseKind.AgentRequested => cause.Code switch
            {
                "customer_asked" or "complaint" => new AgentBlockResult(AgentBlockOutputs.Human, cause.Code),
                AgentHandoffCodes.QuotationDraftCreated => new AgentBlockResult(
                    AgentBlockOutputs.Human, cause.Code, QuotationNumber: quotationNumber, AdvisorMemberId: memberId),
                _ => new AgentBlockResult(AgentBlockOutputs.NoAnswer, cause.Code ?? "no_answer"),
            },
            HandoffCauseKind.AgentLimitReached => new AgentBlockResult(AgentBlockOutputs.Human, cause.Code ?? AgentHandoffCodes.MaxRepliesWithoutHuman),
            // §6.4: con la ventana cerrada no hay a quién mandarle nada; vuelve sin salida.
            HandoffCauseKind.WindowClosed => new AgentBlockResult(null, AgentBlockReasons.WindowClosed),
            HandoffCauseKind.ModuleDisabled => AgentBlockResult.Error(AgentBlockReasons.ModuleDisabled),
            // Fallos del agente o de la cuenta: su código. Sin código, el agente ya no existe (borrado con la sesión viva).
            _ => AgentBlockResult.Error(cause.Code ?? AgentBlockReasons.AgentUnavailable),
        };
    }

    /// <summary>
    /// Devuelve la conversación al flujo y termina la sesión: <c>Returned</c> con la salida, o sin salida <c>WindowClosed</c> o
    /// <c>Interrupted</c> (contacto bloqueado), como hoy (§6.4). Si Mensajería ya no la espera (una persona la tomó en el medio), la sesión
    /// termina <c>Interrupted</c> por la toma. Lo llama quien tiene el candado de la conversación; <c>false</c> si no hubo retorno.
    /// </summary>
    public static async Task<bool> EndAsync(
        IAiConversations conversations, Guid tenantId, AgentSession session, AgentBlockResult result, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(result);
        if (session.DelegationId is not { } delegationId)
        {
            throw new InvalidOperationException("Only a chatbot block session returns to the flow.");
        }

        if (!await TryReturnAsync(conversations, tenantId, session.ConversationId, delegationId, result, cancellationToken))
        {
            session.Finish(AgentSessionStatus.Interrupted, AgentSessionEndReasons.HumanTookOver, null, now);
            return false;
        }

        if (result.Output is { } output)
        {
            session.Return(output, result.Reason, result.Summary, now);
        }
        else
        {
            session.Finish(
                result.Reason == AgentBlockReasons.Blocked ? AgentSessionStatus.Interrupted : AgentSessionStatus.WindowClosed, result.Reason, null, now);
        }

        return true;
    }

    /// <summary>Devolver la conversación al chatbot. Mensajería lo confirma en su propio guardado; si la delegación ya no es la viva de la
    /// conversación (una persona la tomó en el medio) no hay a quién devolver y responde <c>false</c>.</summary>
    public static async Task<bool> TryReturnAsync(
        IAiConversations conversations, Guid tenantId, Guid conversationId, Guid delegationId, AgentBlockResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            await conversations.ReturnToChatbotAsync(tenantId, conversationId, delegationId, result.ToJson(), cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is DomainException or ResourceNotFoundException)
        {
            // La conversación se borró con su cliente, o ya no tiene esa delegación: nadie la espera de vuelta.
            return false;
        }
    }
}
