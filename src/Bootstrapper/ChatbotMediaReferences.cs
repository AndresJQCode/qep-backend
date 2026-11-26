using Microsoft.EntityFrameworkCore;
using Modules.Automations.Infrastructure.Persistence;
using Modules.Messaging.Application;
using Modules.Storage.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-18 («Lifecycle») y regla 2 del owner: qué archivos de chatbot siguen en uso, para el barrido diario de Storage y el aviso de
/// un chatbot borrado. En uso = lo nombra un <c>message.media.fileId</c> del borrador o de una versión de un chatbot del tenant, o de una
/// plantilla guardada del tenant (una plantilla es algo que se conserva), o un mensaje de la bandeja que un bot mandó con él
/// (<see cref="IBotMediaReferences"/>: la conversación sigue mostrándolo).
/// <para>
/// <b>Acoplamiento por nombre</b>: lee <c>automations.chatbots.draft_flow</c>, <c>automations.chatbot_versions.flow</c> y
/// <c>automations.chatbot_templates.flow</c> (el JSON de <c>FlowJson</c>: <c>nodes[].kind = 'message'</c>, <c>nodes[].media.fileId</c>) con
/// SQL, como <see cref="AiChatbotUsage"/>. Los ids se comparan como texto en minúscula (<c>FlowJson</c> escribe el Guid en formato
/// <c>D</c> en minúscula): un valor mal formado en un flujo viejo nunca hace fallar un cast. Sin índice: es un tenant, una vez por página.
/// </para>
/// </summary>
internal sealed class ChatbotMediaReferences(AutomationsDbContext automations, IBotMediaReferences messages) : IChatbotMediaReferences
{
    public async Task<IReadOnlySet<Guid>> FindReferencedAsync(
        Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var idsAsLowerText = fileIds.Select(fileId => fileId.ToString("D")).Distinct(StringComparer.Ordinal).ToArray();
        var rows = await automations.Database.SqlQuery<string>(
            $"""
            SELECT DISTINCT node->'media'->>'fileId' AS "Value"
            FROM (SELECT draft_flow AS flow FROM automations.chatbots WHERE tenant_id = {tenantId}
                  UNION ALL SELECT flow FROM automations.chatbot_versions WHERE tenant_id = {tenantId}
                  UNION ALL SELECT flow FROM automations.chatbot_templates WHERE tenant_id = {tenantId}) AS f
            CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(f.flow->'nodes') = 'array' THEN f.flow->'nodes' ELSE '[]'::jsonb END) AS node
            WHERE node->>'kind' = 'message' AND node->'media'->>'fileId' = ANY({idsAsLowerText})
            """).ToListAsync(cancellationToken);

        var referenced = rows.Select(Guid.Parse).ToHashSet();
        var unclaimed = fileIds.Where(fileId => !referenced.Contains(fileId)).ToList();
        if (unclaimed.Count > 0)
        {
            referenced.UnionWith(await messages.FindReferencedAsync(tenantId, unclaimed, cancellationToken));
        }

        return referenced;
    }
}
