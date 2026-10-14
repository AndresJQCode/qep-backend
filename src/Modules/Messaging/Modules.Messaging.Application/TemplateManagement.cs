using BuildingBlocks.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

// Spec 2026-10-14 §4.3: la administración de plantillas. Todo handler revalida tenant, permiso y módulo antes de tocar nada (403 con
// código, nunca 404 ante otro tenant) y resuelve la conexión con ResolveTemplateSourceAsync (null = connection_unavailable, 422).

internal static class TemplateManagementSupport
{
    public static MessagingDomainException ConnectionUnavailable() =>
        new(MessagingErrorCodes.ConnectionUnavailable, "The WhatsApp connection is paused, needs attention or was deleted.");

    public static ResourceNotFoundException NotFound(Guid templateId) =>
        new(MessagingErrorCodes.TemplateNotFound, $"Template '{templateId}' was not found in this connection.");
}

/// <summary>§4.3: una fila del espejo al DTO de la pantalla, con el texto resuelto por <see cref="TemplateCatalog.FromStored"/>.</summary>
internal static class ManagedTemplateMapping
{
    public static ManagedTemplateDto ToDto(MessageTemplate template, IReadOnlyDictionary<Guid, string> memberNames)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(memberNames);
        var described = TemplateCatalog.FromStored(template.Name, template.Language, template.Category, template.ParameterFormat, template.ComponentsJson);
        return new ManagedTemplateDto(
            template.Id,
            template.MetaTemplateId,
            template.Name,
            template.Language,
            template.Category,
            described.ParameterFormat,
            template.Status,
            template.StatusReason,
            template.Kind.ToString(),
            described.Header,
            described.Body,
            described.Footer,
            described.Buttons,
            described.Parameters,
            described.Sendable,
            described.UnsupportedReason,
            template.CreatedByMemberId is { } member ? new MemberRefDto(member, memberNames.GetValueOrDefault(member) ?? MessageMapping.DeletedMemberName) : null,
            template.CreatedAt,
            template.UpdatedAt);
    }
}

/// <summary>§4.3: arma <see cref="TemplateManagementDto"/> con una llamada a cada directorio. Público sólo para que Infrastructure lo
/// registre; no es API para otros módulos.</summary>
public sealed class TemplateManagementReader(IMessageTemplateRepository repository, IMessagingConnectionDirectory connections, IMessagingMemberNames memberNames)
{
    public async Task<TemplateManagementDto> ReadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = (await connections.ListActiveAsync(tenantId, cancellationToken)).FirstOrDefault(active => active.Id == connectionId)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var templates = await repository.ListAsync(tenantId, connectionId, cancellationToken);
        var creators = templates.Where(template => template.CreatedByMemberId is not null).Select(template => template.CreatedByMemberId!.Value).Distinct().ToArray();
        var names = creators.Length == 0
            ? new Dictionary<Guid, string>()
            : await memberNames.FindAsync(tenantId, creators, cancellationToken);
        var quotation = templates.FirstOrDefault(template => template.Kind == MessageTemplateKind.Quotation);
        return new TemplateManagementDto(
            connection,
            quotation is null ? null : new QuotationTemplateStatusDto(quotation.Id, quotation.Status, quotation.StatusReason),
            await repository.LastSyncAsync(tenantId, connectionId, cancellationToken),
            templates.Select(template => ManagedTemplateMapping.ToDto(template, names)).ToArray());
    }
}

/// <summary>§4.7 <c>GET /connections/{connectionId}/template-management</c>. Si la conexión nunca se sincronizó, la primera lectura llena el
/// espejo (igual que el catálogo de envío, y como él ignora la carrera de dos primeros llenados): la pantalla no puede mostrar «cero plantillas» de un WABA que tiene veinte.</summary>
public sealed record GetTemplateManagementQuery(Guid TenantId, Guid ConnectionId) : IQuery<TemplateManagementDto>;

public sealed class GetTemplateManagementHandler(
    TemplateManagementReader reader,
    IMessageTemplateRepository repository,
    TemplateMirrorSync sync,
    IMessagingConnectionDirectory connections,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetTemplateManagementQuery, TemplateManagementDto>
{
    public async Task<TemplateManagementDto> HandleAsync(GetTemplateManagementQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(query.TenantId, query.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        if (await repository.LastSyncAsync(query.TenantId, query.ConnectionId, cancellationToken) is null)
        {
            try
            {
                await sync.RunAsync(query.TenantId, query.ConnectionId, source, cancellationToken);
            }
            catch (RequestConcurrencyException)
            {
                // La otra primera sincronización ganó y ya llenó el espejo (igual que MirroredTemplateCatalog): se sigue leyendo de ahí.
                // El POST …/sync explícito sí deja subir el 412.
            }
        }

        return await reader.ReadAsync(query.TenantId, query.ConnectionId, cancellationToken);
    }
}

/// <summary>§4.7 <c>POST …/template-management/sync</c>: sincroniza contra Meta y devuelve la pantalla.</summary>
public sealed record SyncTemplatesCommand(Guid TenantId, Guid ConnectionId) : ICommand<TemplateManagementDto>;

public sealed class SyncTemplatesHandler(
    TemplateManagementReader reader,
    TemplateMirrorSync sync,
    IMessagingConnectionDirectory connections,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : ICommandHandler<SyncTemplatesCommand, TemplateManagementDto>
{
    public async Task<TemplateManagementDto> HandleAsync(SyncTemplatesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        await sync.RunAsync(command.TenantId, command.ConnectionId, source, cancellationToken);
        return await reader.ReadAsync(command.TenantId, command.ConnectionId, cancellationToken);
    }
}
