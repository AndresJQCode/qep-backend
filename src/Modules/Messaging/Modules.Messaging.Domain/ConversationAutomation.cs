using System.Text.RegularExpressions;
using BuildingBlocks.Domain;

namespace Modules.Messaging.Domain;

/// <summary>Spec 2026-10-21 §4.2: si un asistente (bot) está atendiendo la conversación. <c>None</c> es lo normal: la atiende una
/// persona o nadie. <c>Paused</c> es un asistente que existió y un humano detuvo; sólo <c>ResumeAutomation</c> lo reactiva.</summary>
public enum ConversationAutomationStatus
{
    None,
    Active,
    Paused,
}

/// <summary>Spec 2026-10-21 §4.2: por qué se pausó el asistente. Viaja en el evento <c>AutomationPaused</c> y se guarda por nombre.</summary>
public enum AutomationPauseReason
{
    AdvisorTook,
    AdvisorReplied,
    AdvisorTransferred,
    Manual,
    Handoff,
}

/// <summary>Spec 2026-10-24: de qué camino salió un traspaso del asistente. Se guarda por nombre.</summary>
public enum AutomationHandoffKind
{
    /// <summary>El flujo llegó a un bloque «Pasar a un asesor».</summary>
    Block,

    /// <summary>Botones o Lista con <c>fallback = handoff</c> y una respuesta inválida.</summary>
    InvalidReply,

    /// <summary>Mensajería rechazó un envío del bot.</summary>
    SendFailed,

    /// <summary>El flujo falló (ciclo, bloque inexistente, versión borrada).</summary>
    FlowFailed,

    /// <summary>Spec 2026-10-25 D-1: el asistente agotó los reintentos de una pregunta y el traspaso lo decide el destino configurado.</summary>
    AttemptsExhausted,

    /// <summary>Spec 2026-10-26 repetir R-4c: el cliente no aceptó la política de datos (al tope de <c>repeat</c> o en <c>handoff</c>).</summary>
    DataConsentRejected,

    /// <summary>Spec 2026-10-26 repetir R-4b: la política de datos no se pudo preguntar porque no hay cliente al que registrarle la respuesta.</summary>
    DataConsentUnavailable,

    /// <summary>Spec 2026-11-17 adenda D-6: el bloque «Agente IA» volvió por una salida de problema (<c>human</c>, <c>noAnswer</c>,
    /// <c>error</c>) que no estaba conectada; <see cref="AutomationHandoff.Code"/> es la salida y lleva los nombres del chatbot y del agente.</summary>
    AgentBlockOutputUnhandled,

    // Spec 2026-10-29 §4.4: los traspasos de un agente de IA. Su espejo es el HandoffCauseKind de Ai (HandoffCauseKindParityTests); los
    // de arriba son el espejo del de Automations. Se guardan por nombre: no se renombran.

    /// <summary>El modelo llamó <c>handoff_to_human</c>; <see cref="AutomationHandoff.Code"/> lleva su motivo cerrado.</summary>
    AgentRequested,

    /// <summary>D-A21: el agente llegó a <c>maxRepliesWithoutHuman</c>.</summary>
    AgentLimitReached,

    /// <summary>El agente falló: su configuración, su respuesta o un tope de D-A20.</summary>
    AgentFailed,

    /// <summary>La cuenta del proveedor del agente falló (caído, sin saldo, clave rechazada, sin permiso); lleva el proveedor.</summary>
    ProviderFailed,

    /// <summary>Spec 2026-10-29 §6.8: el tenant apagó el módulo <c>ai</c> con la conversación atendida por un agente.</summary>
    ModuleDisabled,

    /// <summary>D-A18: la ventana del canal se cerró antes de que la respuesta saliera.</summary>
    WindowClosed,
}

/// <summary>Spec 2026-10-24 D-T6: el bloque HTTP fallido que arrancó el paso que terminó en traspaso. Sólo códigos y la foto de
/// los títulos: nunca el cuerpo, los headers ni la clave de la API (D-H5, D-H6, D-T3).</summary>
public sealed record AutomationHandoffHttp(
    string? NodeId, string? NodeTitle, string? Outcome, string? ErrorCode, int? Status, string? ConnectionName);

/// <summary>
/// Spec 2026-10-24 D-T7: por qué el asistente pasó la conversación a un asesor. Viaja dentro del evento <c>AutomationPaused</c>
/// con <c>PauseReason = Handoff</c>; la ve el tenant y nunca el cliente (D-T1). Se arma con <see cref="Create"/>, que la limpia
/// (D-T12): aunque un adaptador futuro pasara un mensaje de error, no llega al hilo.
/// </summary>
public sealed partial record AutomationHandoff(
    AutomationHandoffKind Kind,
    string? Code,
    string? NodeId,
    string? NodeTitle,
    string? ConnectionName,
    string? ConnectionReason,
    AutomationHandoffHttp? Http,
    Guid? AssignedMemberId = null,
    string? Provider = null,
    string? ChatbotName = null,
    string? AgentName = null)
{
    /// <summary>Títulos y nombres: <see cref="SnapshotText.Fit"/>. Es el largo del paso del asistente.</summary>
    public const int TextMaxLength = 120;

    /// <summary>Códigos e ids: se descartan si no caben. Un código recortado sería otro código.</summary>
    public const int CodeMaxLength = 64;

    /// <summary>Títulos y nombres se recortan a <see cref="TextMaxLength"/> (en blanco → <c>null</c>); un código que no cumple
    /// <c>^[a-z0-9_.]{1,64}$</c> o un id de más de 64 se guardan <c>null</c>; un status fuera de 100..599 también. Un
    /// <paramref name="http"/> que queda sin nodo ni outcome se descarta entero. Spec 2026-10-25 D-7:
    /// <paramref name="assignedMemberId"/> es el asesor a quien el traspaso asigna la conversación; <see cref="Guid.Empty"/> es «nadie».
    /// Plan 2026-11-12: <paramref name="provider"/> es el proveedor del modelo de un agente (<c>anthropic</c>, <c>openai</c>), con la regla
    /// de los códigos. Spec 2026-11-17 adenda D-6: <paramref name="chatbotName"/> y <paramref name="agentName"/> son la foto de los nombres
    /// en un traspaso por una salida del bloque «Agente IA», cuyo código es la salida (<see cref="BotDelegationOutputs"/>, en camelCase:
    /// <c>noAnswer</c> no pasaría la regla de los códigos).</summary>
    public static AutomationHandoff Create(
        AutomationHandoffKind kind,
        string? code,
        string? nodeId,
        string? nodeTitle,
        string? connectionName,
        string? connectionReason,
        AutomationHandoffHttp? http,
        Guid? assignedMemberId = null,
        string? provider = null,
        string? chatbotName = null,
        string? agentName = null) =>
        new(kind,
            kind == AutomationHandoffKind.AgentBlockOutputUnhandled && code is not null && BotDelegationOutputs.IsValid(code) ? code : CleanCode(code),
            CleanId(nodeId), CleanText(nodeTitle), CleanText(connectionName), CleanCode(connectionReason), CleanHttp(http),
            assignedMemberId == Guid.Empty ? null : assignedMemberId, CleanCode(provider), CleanText(chatbotName), CleanText(agentName));

    /// <summary>Si <paramref name="code"/> sobrevive a la limpieza de <see cref="Create"/>. Lo usan las pruebas de paridad de los códigos
    /// que otros módulos mandan.</summary>
    public static bool IsValidCode(string? code) => CleanCode(code) is not null;

    private static AutomationHandoffHttp? CleanHttp(AutomationHandoffHttp? http)
    {
        if (http is null)
        {
            return null;
        }

        var nodeId = CleanId(http.NodeId);
        var outcome = CleanCode(http.Outcome);
        if (nodeId is null && outcome is null)
        {
            return null;
        }

        return new AutomationHandoffHttp(
            nodeId,
            CleanText(http.NodeTitle),
            outcome,
            CleanCode(http.ErrorCode),
            http.Status is >= 100 and <= 599 ? http.Status : null,
            CleanText(http.ConnectionName));
    }

    private static string? CleanText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : SnapshotText.Fit(value.Trim(), TextMaxLength);

    private static string? CleanCode(string? value) => value is not null && CodePattern().IsMatch(value) ? value : null;

    private static string? CleanId(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > CodeMaxLength ? null : value;

    [GeneratedRegex("^[a-z0-9_.]{1,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
