using BuildingBlocks.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.Application;

/// <summary>Una variable de la lista cerrada de una plantilla del sistema, tal como la pantalla la dibuja: clave, etiqueta y ejemplo.</summary>
public sealed record SystemTemplateVariableDto(string Key, string Label, string Example);

/// <summary>
/// El borrador que abre la ventana de edición de una plantilla del sistema. BFF: <c>Body</c> y <c>Locked</c> viajan ya resueltos porque la
/// pantalla no conoce el formato de Meta (un <c>{{1}}</c> de una fila vieja) ni las reglas de adopción de una externa; si calculara el
/// texto inicial, tendría que copiar esas reglas y divergiría sin que nada se ponga rojo. <c>Locked</c> = la fila ya está viva en Meta y su
/// texto no se edita (Meta no lo permite); <c>RejectionReason</c> conserva el motivo de la fila rechazada o pausada de la que se parte.
/// </summary>
public sealed record SystemTemplateDraftDto(string Body, bool Locked, string? RejectionReason, IReadOnlyList<SystemTemplateVariableDto> Variables);

/// <summary>Los dos borradores de cobro en una llamada: la ventana de edición abre los dos a la vez.</summary>
public sealed record BillingTemplatesDraftDto(SystemTemplateDraftDto Reminder, SystemTemplateDraftDto Overdue);

/// <summary>Arma el borrador de una plantilla del sistema a partir del espejo.</summary>
internal static class SystemTemplateDrafts
{
    /// <summary>
    /// Con <paramref name="current"/> viva, su cuerpo y bloqueado. Con <paramref name="current"/> rechazada o pausada, su cuerpo si no está
    /// vacío y todas sus variables están en la lista; si no (claves viejas, <c>{{1}}</c>), el texto base, conservando su motivo. Sin
    /// <paramref name="current"/>, el cuerpo de una <paramref name="external"/> adoptable; si no, el texto base.
    /// </summary>
    public static SystemTemplateDraftDto For(
        MessageTemplate? current, MessageTemplate? external, string baseBody, IReadOnlyList<SystemTemplateVariable> variables)
    {
        ArgumentNullException.ThrowIfNull(baseBody);
        ArgumentNullException.ThrowIfNull(variables);
        var list = variables.Select(variable => new SystemTemplateVariableDto(variable.Key, variable.Label, variable.Example)).ToArray();

        if (current is not null)
        {
            var described = Describe(current);
            if (current.IsLive)
            {
                return new SystemTemplateDraftDto(described.Body, true, null, list);
            }

            var allowed = SystemTemplateVariables.KeysOf(variables);
            var usable = !string.IsNullOrWhiteSpace(described.Body) && described.Parameters.All(allowed.Contains);
            return new SystemTemplateDraftDto(usable ? described.Body : baseBody, false, current.StatusReason, list);
        }

        if (external is not null && QuotationTemplateDefinition.IsAdoptable(external.ComponentsJson))
        {
            return new SystemTemplateDraftDto(Describe(external).Body, false, null, list);
        }

        return new SystemTemplateDraftDto(baseBody, false, null, list);
    }

    private static MessageTemplateDto Describe(MessageTemplate template) =>
        TemplateCatalog.FromStored(template.Name, template.Language, template.Category, template.ParameterFormat, template.ComponentsJson);
}

/// <summary><c>GET …/template-management/quotation/draft</c>: el borrador de la plantilla de cotización de la conexión.</summary>
public sealed record GetQuotationTemplateDraftQuery(Guid TenantId, Guid ConnectionId) : IQuery<SystemTemplateDraftDto>;

public sealed class GetQuotationTemplateDraftHandler(
    IMessageTemplateRepository repository,
    IMessagingConnectionDirectory connections,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetQuotationTemplateDraftQuery, SystemTemplateDraftDto>
{
    public async Task<SystemTemplateDraftDto> HandleAsync(GetQuotationTemplateDraftQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        _ = await connections.ResolveTemplateSourceAsync(query.TenantId, query.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var current = await repository.FindQuotationAsync(query.TenantId, query.ConnectionId, cancellationToken);
        var external = current is null
            ? await repository.FindByNameAsync(query.TenantId, query.ConnectionId, QuotationTemplateDefinition.Name, QuotationTemplateDefinition.Language, cancellationToken)
            : null;
        return SystemTemplateDrafts.For(current, external, QuotationTemplateDefinition.BaseBody, SystemTemplateVariables.Quotation);
    }
}

/// <summary><c>GET …/template-management/billing/draft</c>: los borradores de las dos plantillas de cobro de la conexión.</summary>
public sealed record GetBillingTemplatesDraftQuery(Guid TenantId, Guid ConnectionId) : IQuery<BillingTemplatesDraftDto>;

public sealed class GetBillingTemplatesDraftHandler(
    IMessageTemplateRepository repository,
    IMessagingConnectionDirectory connections,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetBillingTemplatesDraftQuery, BillingTemplatesDraftDto>
{
    public async Task<BillingTemplatesDraftDto> HandleAsync(GetBillingTemplatesDraftQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        // Las plantillas de cobro son de Billing: sin su módulo no se ofrece su borrador.
        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, query.TenantId, TenantModuleKeys.ServiceContracts, cancellationToken);
        _ = await connections.ResolveTemplateSourceAsync(query.TenantId, query.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var mirror = await repository.ListAsync(query.TenantId, query.ConnectionId, cancellationToken);
        return new BillingTemplatesDraftDto(DraftOf(mirror, BillingTemplateDefinitions.Reminder), DraftOf(mirror, BillingTemplateDefinitions.Overdue));
    }

    private static SystemTemplateDraftDto DraftOf(IReadOnlyList<MessageTemplate> mirror, BillingTemplateDefinition definition) =>
        SystemTemplateDrafts.For(mirror.FirstOrDefault(template => template.Kind == definition.Kind), null, definition.BaseBody, definition.Variables);
}
