namespace Modules.Ai.Domain;

public enum AgentIssueSeverity
{
    Error,
    Warning,
}

/// <summary>Spec 2026-10-29 §4.6: un problema del <c>config</c>, con la ruta del campo (la que el editor marca), como <c>FlowIssue</c>.</summary>
public sealed record AgentIssue(AgentIssueSeverity Severity, string Code, string Path, string Message);

/// <summary>Lo que dice la lista curada del modelo elegido (plan 2026-11-12 C-3, C-16).</summary>
public enum AgentModelAvailability
{
    /// <summary>Está en la lista de su proveedor y se puede elegir.</summary>
    Available,

    /// <summary>El proveedor existe pero el modelo no está en su lista, o está retirado (<c>available: false</c>).</summary>
    Unavailable,

    /// <summary>El proveedor no es ninguno de <see cref="AiProviders.All"/>.</summary>
    UnknownProvider,
}

/// <summary>Lo que el validador necesita y el <c>config</c> no tiene: el resto del agente y lo que saben otros módulos, resuelto por
/// el handler.</summary>
/// <param name="HandoffMemberCanReply"><c>null</c> si el traspaso no es a un miembro o no tiene miembro.</param>
/// <param name="Publishing">Publicar endurece: un modelo fuera de la lista pasa de aviso a error (plan 2026-11-12 C-16).</param>
public sealed record AgentValidationContext(int ConnectionCount, AgentModelAvailability Model, bool? HandoffMemberCanReply, bool Publishing);

/// <summary>
/// Spec 2026-10-29 D-A7: los problemas que impiden publicar. Guardar nunca falla por ellos: el borrador puede quedar a medias y la
/// respuesta los lista. Los topes y los valores cerrados no están acá: los rechaza el endpoint con su campo antes de guardar.
/// <c>tools.quotationDraft</c> sin asesor queda fuera hasta que el owner resuelva P-8 del spec.
/// </summary>
public static class AgentConfigValidator
{
    public static IReadOnlyList<AgentIssue> Validate(AgentConfig config, AgentValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(context);
        var issues = new List<AgentIssue>();

        if (string.IsNullOrWhiteSpace(config.Instructions))
        {
            issues.Add(Error("instructions_missing", "instructions", "Escribe las instrucciones del agente."));
        }

        if (context.ConnectionCount == 0)
        {
            issues.Add(Error("connections_missing", "connections", "Elige al menos una conexión para que el agente atienda."));
        }

        switch (context.Model)
        {
            case AgentModelAvailability.UnknownProvider:
                issues.Add(Error("model_provider_unknown", "model.provider", "Elige un proveedor de la lista."));
                break;
            case AgentModelAvailability.Unavailable:
                issues.Add(new AgentIssue(
                    context.Publishing ? AgentIssueSeverity.Error : AgentIssueSeverity.Warning,
                    "model_unavailable",
                    "model",
                    "El modelo elegido ya no está disponible: elige otro para publicar."));
                break;
        }

        if (config.Handoff.Destination == AgentHandoff.ToMember)
        {
            if (config.Handoff.MemberId is null)
            {
                issues.Add(Error("handoff_member_missing", "handoff.memberId", "Elige a quién se le pasa la conversación."));
            }
            else if (context.HandoffMemberCanReply == false)
            {
                issues.Add(Error(
                    "handoff_member_cannot_reply", "handoff.memberId", "Esa persona ya no puede responder conversaciones: elige otra."));
            }
        }

        return issues;
    }

    private static AgentIssue Error(string code, string path, string message) => new(AgentIssueSeverity.Error, code, path, message);
}
