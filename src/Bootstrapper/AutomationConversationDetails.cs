using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modules.Automations.Application;
using Modules.Automations.Domain.Flow;
using Modules.Automations.Infrastructure.Persistence;
using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-21 §5.5: «Datos capturados» del asesor. Lee la corrida más reciente de la conversación por el repositorio de
/// Automations y le suma el nombre del chatbot y el número de la versión con que corrió (D-C1), que viven en las tablas de Automations. Se
/// llama <c>AutomationConversationDetails</c> y no como el record del puerto para no tapar su nombre dentro de este namespace.</summary>
internal sealed class AutomationConversationDetails(IConversationRunRepository runs, AutomationsDbContext context) : IConversationAutomationDetails
{
    private static readonly JsonSerializerOptions TraceOptions = new(JsonSerializerDefaults.Web);

    public async Task<ConversationAutomationDetails?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        var run = await runs.FindLatestAsync(tenantId, conversationId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        // Un chatbot borrado se lleva sus versiones (cascade): la corrida sigue ahí y el asesor la ve con el nombre genérico que ya usa la bandeja.
        var chatbotName = await context.Chatbots.AsNoTracking()
            .Where(chatbot => chatbot.TenantId == run.TenantId && chatbot.Id == run.ChatbotId)
            .Select(chatbot => chatbot.Name)
            .SingleOrDefaultAsync(cancellationToken);
        var version = await context.ChatbotVersions.AsNoTracking()
            .Where(candidate => candidate.TenantId == run.TenantId && candidate.Id == run.ChatbotVersionId)
            .Select(candidate => new { candidate.Number, candidate.FlowJson })
            .SingleOrDefaultAsync(cancellationToken);

        var trace = (JsonSerializer.Deserialize<List<TraceRow>>(run.TraceJson, TraceOptions) ?? [])
            .Select(row => new ConversationAutomationTraceEntry(row.At, row.NodeId, row.Kind, row.Outcome))
            .ToList();
        return new ConversationAutomationDetails(
            run.ChatbotId,
            chatbotName ?? "Chatbot",
            version?.Number ?? 0,
            run.Status.ToString(),
            run.IsLive && version is not null ? StepTitleOf(version.FlowJson, run.CurrentNodeId) : null,
            JsonSerializer.Deserialize<JsonElement>(run.VariablesJson),
            trace,
            run.StartedAt,
            run.EndedAt);
    }

    // El título del bloque en que espera la corrida: lo mismo que el motor le da a la bandeja como paso (WaitForCustomer.StepTitle).
    private static string? StepTitleOf(string flowJson, string? nodeId)
    {
        if (nodeId is null)
        {
            return null;
        }

        try
        {
            var title = FlowJson.Parse(flowJson).Nodes.FirstOrDefault(node => node.Id == nodeId)?.Title;
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (FlowFormatException)
        {
            return null;
        }
    }

    private sealed record TraceRow(DateTimeOffset At, string NodeId, string Kind, string Outcome);
}
