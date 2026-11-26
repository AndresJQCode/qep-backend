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
/// <param name="QuotationAdvisorCanQuote"><c>null</c> si la herramienta de cotización está apagada o no tiene asesora.</param>
/// <param name="QuotationBillingAccountEligible">Si la cuenta guardada sigue entre las que el editor ofrece (plan 2026-11-17 A3); <c>null</c>
/// si la herramienta de cotización está apagada o no tiene cuenta.</param>
public sealed record AgentValidationContext(
    int ConnectionCount,
    AgentModelAvailability Model,
    bool? HandoffMemberCanReply,
    bool Publishing,
    bool? QuotationAdvisorCanQuote = null,
    bool? QuotationBillingAccountEligible = null,
    AgentSchedulingReadiness? Scheduling = null);

/// <summary>Spec 2026-11-23 §6.3 y §9.2: si la Agenda puede atender al agente. Lo calcula Scheduling (por el puerto de Ai) y el editor lo
/// muestra; el validador lo exige sólo con <c>tools.scheduling</c> prendido.</summary>
/// <param name="ModuleEnabled">El módulo <c>scheduling</c> está prendido para el tenant.</param>
/// <param name="BookableServices">Servicios activos con «Se puede reservar por WhatsApp» que alguna franja activa ofrece con algún recurso activo.</param>
public sealed record AgentSchedulingReadiness(bool ModuleEnabled, int BookableServices)
{
    public bool Ready => ModuleEnabled && BookableServices > 0;
}

/// <summary>
/// Spec 2026-10-29 D-A7: los problemas que impiden publicar. Guardar nunca falla por ellos: el borrador puede quedar a medias y la
/// respuesta los lista. Los topes y los valores cerrados no están acá: los rechaza el endpoint con su campo antes de guardar.
/// <c>tools.quotationDraft</c> exige asesora y cuenta de cobro (P-4); una asesora que ya no puede cotizar es aviso, no error: la herramienta
/// queda bloqueada al correr (responde <c>quotation_advisor_unavailable</c>) hasta que se elija otra.
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

        // Spec 2026-11-17 D-B6: un agente sólo para bloques de chatbots no ocupa conexión propia; sin conexiones se publica y sólo se avisa.
        if (context.ConnectionCount == 0)
        {
            issues.Add(new AgentIssue(
                AgentIssueSeverity.Warning,
                "connections_missing",
                "connections",
                "El agente no tiene conexiones: sólo atenderá cuando un chatbot lo llame desde un bloque «Agente IA»."));
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

        if (config.Tools.QuotationDraft)
        {
            if (config.QuotationDraft?.AdvisorMemberId is null)
            {
                issues.Add(Error(
                    "quotation_advisor_missing", "quotationDraft.advisorMemberId",
                    "Elige quién queda como asesor de los borradores de cotización."));
            }
            else if (context.QuotationAdvisorCanQuote == false)
            {
                issues.Add(new AgentIssue(
                    AgentIssueSeverity.Warning,
                    "quotation_advisor_cannot_quote",
                    "quotationDraft.advisorMemberId",
                    "Esa persona ya no puede cotizar: el agente no creará borradores hasta que elijas a otra."));
            }

            if (config.QuotationDraft?.BillingAccount is null)
            {
                issues.Add(Error(
                    "quotation_billing_account_missing", "quotationDraft.billingAccount",
                    "Elige la cuenta de cobro de los borradores de cotización."));
            }
            else if (context.QuotationBillingAccountEligible == false)
            {
                // Plan 2026-11-17 A3: mismo criterio que la asesora. Al correr, Quotations la rechaza con
                // quotation.billing.account_not_found y la herramienta vuelve al modelo con el código.
                issues.Add(new AgentIssue(
                    AgentIssueSeverity.Warning,
                    "quotation_billing_account_unavailable",
                    "quotationDraft.billingAccount",
                    "Esa cuenta de cobro ya no está disponible: el agente no creará borradores hasta que elijas otra."));
            }
        }

        if (config.Tools.Scheduling && context.Scheduling is { } scheduling)
        {
            if (!scheduling.ModuleEnabled)
            {
                issues.Add(Error("scheduling_module_disabled", "tools.scheduling", "Activa el módulo Agenda para que el agente pueda agendar citas."));
            }
            else if (scheduling.BookableServices == 0)
            {
                issues.Add(Error(
                    "scheduling_no_bookable_service",
                    "tools.scheduling",
                    "Ningún servicio de la Agenda se puede reservar por WhatsApp en una franja activa: marca uno como «Se puede reservar por WhatsApp» y ofrécelo en una franja."));
            }
        }

        return issues;
    }

    private static AgentIssue Error(string code, string path, string message) => new(AgentIssueSeverity.Error, code, path, message);
}
