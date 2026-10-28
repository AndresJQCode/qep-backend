using System.Globalization;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-14 §4.3 «Refrescar»: el listado completo de Meta hace upsert en el espejo por <c>meta_template_id</c> (<c>SyncFrom</c> en
/// las conocidas, <c>Mirrored</c> en las nuevas), elimina las filas que Meta ya no lista, escribe <c>template_mirror_syncs</c> y guarda
/// **una** vez. Con un listado incompleto (<see cref="TemplateListing.Complete"/>) no elimina nada, y una fila creada o actualizada
/// después de empezar a listar no se elimina ni se pisa (revisión final F4 y F5). Un elemento sin <c>id</c>, nombre o idioma se salta;
/// un <c>id</c> repetido en la misma lista se toma una vez (Review Focus 3). Público para que Infrastructure lo registre; no es API para
/// otros módulos.
/// <para>
/// Un listado que falla no es una creación: el token vencido o un permiso faltante reportan la conexión y son
/// <c>messaging.connection_unavailable</c> (<see cref="TemplateGraphErrors.FailAsync"/>); <b>cualquier otra</b> falla es
/// <c>messaging.templates.unavailable</c>, el contrato de <see cref="IMessageTemplates"/>. Nunca <see cref="TemplateGraphErrors.Translate"/>:
/// un 400 del listado no es un nombre repetido ni un rechazo de la plantilla.
/// </para>
/// </summary>
public sealed partial class TemplateMirrorSync(
    IMessageTemplateRepository repository,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingUnitOfWork unitOfWork,
    IClock clock,
    ILogger<TemplateMirrorSync> logger)
{
    public async Task RunAsync(Guid tenantId, Guid connectionId, MessagingTemplateSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Revisión final F5: el corte es antes de llamar a Meta. Una fila creada o actualizada después (el webhook, otro aprovisionamiento)
        // es más nueva que el listado y no se borra ni se pisa con lo que Meta decía al empezar.
        var listedAt = clock.UtcNow;
        var result = await meta.ListTemplatesAsync(source, cancellationToken);
        if (!result.Succeeded || result.Value is null)
        {
            var failure = result.Failure ?? new MessagingGraphFailure(0, null, "unreachable");
            if (TemplateGraphErrors.IsTokenRejected(failure))
            {
                throw await TemplateGraphErrors.FailAsync(connections, tenantId, connectionId, failure, cancellationToken);
            }

            throw new MessagingDomainException(MessagingErrorCodes.TemplatesUnavailable, "WhatsApp did not answer the list of templates.");
        }

        var now = clock.UtcNow;
        var known = (await repository.ListAsync(tenantId, connectionId, cancellationToken)).ToDictionary(row => row.MetaTemplateId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in result.Value.Templates)
        {
            if (raw.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = Identifier(raw, "id");
            var name = TemplateCatalog.Text(raw, "name");
            var language = TemplateCatalog.Text(raw, "language");
            if (id is null || id.Length > MessageTemplate.MetaTemplateIdMaxLength || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(language) || !seen.Add(id))
            {
                continue;
            }

            var components = raw.TryGetProperty("components", out var list) && list.ValueKind == JsonValueKind.Array ? list.GetRawText() : "[]";
            var category = TemplateCatalog.Text(raw, "category") ?? string.Empty;
            var format = TemplateCatalog.Text(raw, "parameter_format") ?? "POSITIONAL";
            var status = TemplateCatalog.Text(raw, "status");
            var reason = TemplateCatalog.Text(raw, "rejected_reason");
            if (known.TryGetValue(id, out var existing))
            {
                if (!ChangedSince(existing, listedAt))
                {
                    existing.SyncFrom(status, reason, category, format, components, now);
                }
            }
            else
            {
                repository.Add(MessageTemplate.Mirrored(tenantId, connectionId, id, name, language, category, format, status, reason, components, now));
            }
        }

        // Revisión final F4: un listado incompleto no dice que lo que falta ya no esté en Meta; se actualiza lo que vino y no se borra nada.
        if (result.Value.Complete)
        {
            foreach (var (id, template) in known)
            {
                if (!seen.Contains(id) && !ChangedSince(template, listedAt))
                {
                    repository.Remove(template);
                }
            }
        }
        else
        {
            LogIncompleteListing(logger, tenantId, connectionId, seen.Count);
        }

        await repository.MarkSyncedAsync(tenantId, connectionId, now, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static bool ChangedSince(MessageTemplate template, DateTimeOffset listedAt) => template.UpdatedAt > listedAt || template.CreatedAt > listedAt;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The template listing of connection {ConnectionId} in tenant {TenantId} came back incomplete ({Listed} templates); the mirror was updated but nothing was removed.")]
    private static partial void LogIncompleteListing(ILogger logger, Guid tenantId, Guid connectionId, int listed);

    /// <summary>Meta manda los ids como string en el listado y como número en el webhook; se aceptan los dos. Público para que el parser
    /// del webhook (<c>message_template_status_update</c>) lo reuse en vez de copiarlo.</summary>
    public static string? Identifier(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }
}

/// <summary>
/// Spec 2026-10-14 D-T8: el catálogo de envío lee del espejo. Si <c>refresh</c> o la conexión nunca se sincronizó, corre
/// <see cref="TemplateMirrorSync"/> primero (el primer llenado es la única excepción a R8 del spec 2026-10-11: ocurre una vez por conexión).
/// La conexión se resuelve siempre: una pausada, borrada o de otro tenant no entrega plantillas aunque el espejo las tenga.
/// <para>
/// Dos primeros llenados a la vez (dos destinatarios de una campaña, dos asesores abriendo la bandeja tras el despliegue) chocan en el
/// marcador o en el <c>id</c> de Meta y el que pierde recibe <see cref="RequestConcurrencyException"/>. En el llenado implícito eso no
/// sube: el otro ya llenó el espejo y se lee de ahí. Con <c>refresh</c> sí sube (412), como cualquier refresco que pierde.
/// </para>
/// </summary>
public sealed class MirroredTemplateCatalog(IMessageTemplateRepository repository, TemplateMirrorSync sync, IMessagingConnectionDirectory connections) : IMessageTemplates
{
    public async Task<IReadOnlyList<MessageTemplateDto>> ListAsync(Guid tenantId, Guid connectionId, bool refresh, CancellationToken cancellationToken)
    {
        var source = await connections.ResolveTemplateSourceAsync(tenantId, connectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        if (refresh)
        {
            await sync.RunAsync(tenantId, connectionId, source, cancellationToken);
        }
        else if (await repository.LastSyncAsync(tenantId, connectionId, cancellationToken) is null)
        {
            try
            {
                await sync.RunAsync(tenantId, connectionId, source, cancellationToken);
            }
            catch (RequestConcurrencyException)
            {
                // La otra sincronización ganó y ya escribió el espejo: se sigue leyendo de ahí.
            }
        }

        var rows = await repository.ListApprovedAsync(tenantId, connectionId, cancellationToken);
        return rows
            .Select(row => TemplateCatalog.FromStored(row.Name, row.Language, row.Category, row.ParameterFormat, row.ComponentsJson))
            .OrderBy(template => template.Name, StringComparer.Ordinal)
            .ThenBy(template => template.Language, StringComparer.Ordinal)
            .ToArray();
    }
}
