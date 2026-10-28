using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// La conexión de WhatsApp que el tenant eligió para enviar sus documentos (spec 2026-10-21 D5–D10): una por tenant,
/// guardada por <c>connection_id</c> y sin llave foránea, porque la conexión vive en Integrations.
/// </summary>
public interface IWhatsAppChannelChoiceStore
{
    /// <summary>La conexión guardada, o <c>null</c> si el tenant nunca eligió. Puede ser una que ya no esté disponible:
    /// quien la lee decide qué hacer con una elección vencida.</summary>
    Task<Guid?> FindAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Inserta, o reemplaza sólo si la fila guardada sigue siendo <paramref name="previous"/> (Decisión 9):
    /// quien eligió mientras otro envío elegía a la vez no pisa lo que el otro acaba de guardar. <c>true</c> si escribió.
    /// Corre dentro de la transacción del llamador.</summary>
    Task<bool> RememberAsync(
        Guid tenantId,
        Guid connectionId,
        Guid? previous,
        Guid chosenByMemberId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Upsert incondicional (el <c>PUT</c> desde Configuración): ahí la persona elige a conciencia y no hay
    /// elección anterior que respetar.</summary>
    Task ReplaceAsync(
        Guid tenantId,
        Guid connectionId,
        Guid chosenByMemberId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <summary>
/// Políticas con nombre propio, para lo que se autoriza con "cualquiera de" varios permisos. Se registra en
/// <c>AddAuthorization</c>; sin ella <c>RequireAuthorization</c> no resuelve y el síntoma es un 500, no un 403.
/// </summary>
public static class QuotationsPolicies
{
    /// <summary>Leer los canales de WhatsApp: quien gestiona cotizaciones (los ofrece al enviar) o quien actualiza la
    /// configuración (los muestra en Configuración, decisión del owner 2026-10-25).</summary>
    public const string WhatsAppChannelsRead = "quotations.whatsapp_channels.read";
}

/// <summary>BFF: <c>NeedsChoice</c> viaja calculado para que la pantalla no repita la regla de la spec 2026-10-21 §4.2;
/// el teléfono va como lo guarda la conexión.</summary>
public sealed record WhatsAppChannelOptionDto(Guid ConnectionId, string Provider, string Name, string? Phone);

/// <summary><c>SelectedConnectionId</c> va sólo si la elección guardada sigue entre los disponibles: una vencida no se
/// reporta, y la pantalla la ve como "sin elegir".</summary>
public sealed record WhatsAppChannelsDto(
    IReadOnlyList<WhatsAppChannelOptionDto> Channels, Guid? SelectedConnectionId, bool NeedsChoice);

public sealed record GetWhatsAppChannelsQuery(Guid TenantId) : IQuery<WhatsAppChannelsDto>;

public sealed record ChooseWhatsAppChannelCommand(Guid TenantId, Guid? ConnectionId) : ICommand<WhatsAppChannelsDto>;

public sealed class ChooseWhatsAppChannelValidator : AbstractValidator<ChooseWhatsAppChannelCommand>
{
    public ChooseWhatsAppChannelValidator()
    {
        // Nullable a propósito: un cuerpo sin el campo llega como null y es un 422 con el campo marcado.
        RuleFor(command => command.ConnectionId)
            .NotNull()
            .NotEqual(Guid.Empty)
            .OverridePropertyName("connectionId");
    }
}

/// <summary>Lo que arma la respuesta de los dos endpoints, para que el <c>PUT</c> devuelva lo mismo que el <c>GET</c>.</summary>
internal static class WhatsAppChannelsMapping
{
    public static WhatsAppChannelsDto ToDto(IReadOnlyList<WhatsAppChannel> available, Guid? saved)
    {
        var selected = available.Any(channel => channel.ConnectionId == saved) ? saved : null;
        return new WhatsAppChannelsDto(
            [.. available.Select(channel => new WhatsAppChannelOptionDto(
                channel.ConnectionId, channel.Provider, channel.Name, channel.Phone))],
            selected,
            NeedsChoice: available.Count > 1 && selected is null);
    }
}

public sealed class GetWhatsAppChannelsHandler(
    IWhatsAppChannelDirectory channels,
    IWhatsAppChannelChoiceStore choices,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetWhatsAppChannelsQuery, WhatsAppChannelsDto>
{
    public async Task<WhatsAppChannelsDto> HandleAsync(
        GetWhatsAppChannelsQuery query, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorizedAny(
            executionContext, query.TenantId, QuotationsPermissions.QuotationManage, TenancyPermissions.SettingsUpdate);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, query.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Quotations, cancellationToken);

        var available = await channels.ListAvailableAsync(query.TenantId, cancellationToken);
        return WhatsAppChannelsMapping.ToDto(available, await choices.FindAsync(query.TenantId, cancellationToken));
    }
}

/// <summary>Elegir el canal desde Configuración: el upsert incondicional, porque ahí la persona elige a conciencia y no
/// hay elección anterior que respetar (la primera elección al enviar, en cambio, viaja con el <c>Sent</c>).</summary>
public sealed class ChooseWhatsAppChannelHandler(
    IWhatsAppChannelDirectory channels,
    IWhatsAppChannelChoiceStore choices,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<ChooseWhatsAppChannelCommand> validator)
    : ICommandHandler<ChooseWhatsAppChannelCommand, WhatsAppChannelsDto>
{
    public const string AuditAction = "quotations.whatsapp_channel.chosen";

    public async Task<WhatsAppChannelsDto> HandleAsync(
        ChooseWhatsAppChannelCommand command, CancellationToken cancellationToken)
    {
        // Se autoriza antes de validar: quien llama desde otro tenant no debe ver el mapa de campos.
        QuotationsAuthorization.EnsureAuthorized(executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, command.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Quotations, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var connectionId = command.ConnectionId!.Value;
        var available = await channels.ListAvailableAsync(command.TenantId, cancellationToken);
        if (available.All(channel => channel.ConnectionId != connectionId))
        {
            throw new QuotationsDomainException(
                "quotation.whatsapp.channel_unavailable",
                "The chosen WhatsApp connection is not available to send documents.");
        }

        var member = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);
        var now = clock.UtcNow;

        // Transacción explícita: el upsert es SQL crudo y la auditoría entra por el outbox del mismo SaveChanges.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await choices.ReplaceAsync(command.TenantId, connectionId, member.Value, now, cancellationToken);
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, AuditAction, connectionId.ToString(), "success", now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return WhatsAppChannelsMapping.ToDto(available, connectionId);
    }
}
