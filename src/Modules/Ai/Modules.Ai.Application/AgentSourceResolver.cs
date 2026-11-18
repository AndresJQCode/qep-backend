using Modules.Ai.Domain;

namespace Modules.Ai.Application;

/// <summary>
/// Spec 2026-10-29 §8.2 y plan 2026-11-17 A4: las fuentes de una respuesta, de ids a algo que se lee (la pregunta frecuente, el atajo de la
/// respuesta rápida, el nombre de la herramienta). Un solo lugar para la actividad real y el playground: la pantalla no cruza la versión del
/// agente ni las respuestas rápidas.
/// </summary>
public static class AgentSourceResolver
{
    public const string FaqSource = "faq";
    public const string QuickReplySource = "quickReply";
    public const string ToolSource = "tool";

    /// <summary>Las preguntas frecuentes de una configuración (la de la versión de la sesión o la foto del borrador de la prueba), por id.</summary>
    public static Dictionary<string, string> FaqOf(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Faq.ToDictionary(entry => entry.Id, entry => entry.Question, StringComparer.Ordinal);
    }

    /// <summary>Los atajos de las respuestas rápidas de hoy, sólo si alguna respuesta tiene fuentes (si no, ni se leen). Una que se borró
    /// después sigue citada, sin atajo.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> ShortcutsAsync(
        IAiQuickReplies quickReplies, Guid tenantId, bool anySources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quickReplies);
        return anySources
            ? (await quickReplies.ListAsync(tenantId, cancellationToken)).ToDictionary(reply => reply.Id, reply => reply.Shortcut, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>§8.2: una fuente es una pregunta frecuente de la configuración, una llamada a herramienta del mismo trabajo o una respuesta
    /// rápida. El bucle ya descartó las que no existían al responder; lo que no se reconoce ahora es una respuesta rápida borrada después.</summary>
    public static AgentActivitySourceDto Resolve(
        string id,
        IReadOnlyDictionary<string, string> faq,
        IReadOnlyDictionary<string, string> shortcuts,
        IReadOnlyList<AgentTestToolCallDto> calls)
    {
        ArgumentNullException.ThrowIfNull(faq);
        ArgumentNullException.ThrowIfNull(shortcuts);
        ArgumentNullException.ThrowIfNull(calls);
        if (faq.TryGetValue(id, out var question))
        {
            return new AgentActivitySourceDto(FaqSource, id, question);
        }

        var call = calls.FirstOrDefault(candidate => candidate.CallId == id);
        if (call is not null)
        {
            return new AgentActivitySourceDto(ToolSource, id, call.Tool);
        }

        return new AgentActivitySourceDto(QuickReplySource, id, shortcuts.GetValueOrDefault(id));
    }
}
