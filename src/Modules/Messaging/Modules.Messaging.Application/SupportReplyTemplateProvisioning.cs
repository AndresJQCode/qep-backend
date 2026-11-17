using System.Globalization;
using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Soporte fase 2: la **única** fuente del nombre, idioma, categoría, texto base y ejemplos de la plantilla «Aviso de respuesta», calcada de
/// <see cref="OrderTemplateDefinition"/> pero sin encabezado. El PDF de la respuesta formal (fase 4b) no es una versión de esta: es otro tipo,
/// <see cref="SupportFormalReplyTemplateDefinition"/>, porque Meta no admite un encabezado opcional. Sale cuando un asesor responde un ticket por WhatsApp fuera de la ventana de 24 h. El admin puede
/// editar el texto al crearla, con las variables de <see cref="SystemTemplateVariables.SupportReply"/>. El nombre va por versiones
/// (<c>qep_soporte_respuesta</c>, <c>qep_soporte_respuesta_v2</c>…) por la misma razón que la cotización: Meta bloquea un tiempo el nombre
/// de una plantilla borrada. El envío lee el nombre vigente de la fila <c>SupportReply</c>, no de esta constante.
/// </summary>
public static class SupportReplyTemplateDefinition
{
    public const string Name = MessageTemplateName.SupportReplyName;
    public const string Language = "es";
    public const string Category = "UTILITY";
    /// <summary>No empieza ni termina con una variable: Meta rechaza un cuerpo así.</summary>
    public const string BaseBody =
        "Hola {{nombre_contacto}}, te escribimos sobre tu caso {{radicado}}: {{respuesta}}. Si tienes dudas, responde este mensaje.";

    /// <summary>Las claves que el envío llena además de las fijas.</summary>
    public const string CaseNumberKey = "radicado";

    public const string ReplyKey = "respuesta";

    // La única clave vieja que el envío sabe llenar (LegacyKeys la traduce a nombre_contacto).
    private const string LegacyContactNameKey = "fullname";

    // «qep_soporte_respuesta_v»: dentro del prefijo reservado, así que ninguna libre puede tomar una versión.
    private const string VersionPrefix = MessageTemplateName.SupportReplyPrefix + "v";

    public static readonly IReadOnlyDictionary<string, string> Examples =
        SystemTemplateVariables.SupportReply.ToDictionary(variable => variable.Key, variable => variable.Example, StringComparer.Ordinal);

    /// <summary>El nombre de la versión <paramref name="version"/>: la 1 es <see cref="Name"/>; de la 2 en adelante, <c>qep_soporte_respuesta_v{n}</c>.</summary>
    public static string NameFor(int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return version == 1 ? Name : VersionPrefix + version.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>El inverso de <see cref="NameFor"/>, con las mismas reglas que la de pedido: cualquier otro nombre es <c>null</c>.</summary>
    public static int? VersionOf(string? name)
    {
        if (string.Equals(name, Name, StringComparison.Ordinal))
        {
            return 1;
        }

        if (name is null || !name.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var digits = name[VersionPrefix.Length..];
        if (digits.Length == 0 || digits[0] == '0' || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version >= 2 ? version : null;
    }

    /// <summary>Sólo cuerpo: sin encabezado ni pie.</summary>
    public static TemplateDraft Draft(string name, string body) => new(name, Language, Category, null, body, null, [], Examples);

    public static JsonObject Payload(string name, string body) => TemplateComponentsBuilder.CreatePayload(Draft(name, body));

    /// <summary>Una externa se adopta si tiene sólo cuerpo (sin encabezado de ningún tipo ni botones), no vacío, y sus variables son de la
    /// lista de Soporte o <c>fullname</c>. De las claves viejas, el envío sólo sabe llenar esa (la traduce a <c>nombre_contacto</c>): una
    /// <c>order_number</c> o <c>valid_until</c> quedaría sin valor y la plantilla nunca saldría. Mira la forma, no el texto.</summary>
    public static bool IsAdoptable(string componentsJson)
    {
        ArgumentNullException.ThrowIfNull(componentsJson);
        var described = TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson);
        var allowed = SystemTemplateVariables.KeysOf(SystemTemplateVariables.SupportReply);
        return !string.IsNullOrWhiteSpace(described.Body)
            && described.Header is null
            && described.UnsupportedReason is null
            && described.Parameters.All(key => allowed.Contains(key) || string.Equals(key, LegacyContactNameKey, StringComparison.Ordinal))
            && described.Buttons.Count == 0;
    }

    internal static string? StoredBody(string componentsJson) =>
        TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson).Body?.Trim();
}

/// <summary>Soporte fase 2, <c>POST …/template-management/support-reply</c>: aprovisiona (o adopta, o reintenta) la plantilla «Aviso de respuesta».</summary>
/// <param name="Body">El cuerpo editado, o <c>null</c> para el texto base.</param>
public sealed record ProvisionSupportReplyTemplateCommand(Guid TenantId, Guid ConnectionId, string? Body = null) : ICommand<ManagedTemplateDto>;

/// <summary>El cuerpo, si viene, usa sólo las variables de Soporte.</summary>
public sealed class ProvisionSupportReplyTemplateCommandValidator : AbstractValidator<ProvisionSupportReplyTemplateCommand>
{
    public ProvisionSupportReplyTemplateCommandValidator() =>
        RuleFor(command => command.Body).Custom((body, context) =>
        {
            if (body is not null && TemplateBodyRules.Check(body, SystemTemplateVariables.KeysOf(SystemTemplateVariables.SupportReply)) is { } error)
            {
                context.AddFailure(new ValidationFailure("body", error));
            }
        });
}

/// <summary>
/// Soporte fase 2: el mismo procedimiento que <see cref="ProvisionOrderTemplateHandler"/>, paso por paso, con la fila <c>SupportReply</c> y
/// sin subir un ejemplo (no hay encabezado): viva ⇒ <c>support_reply_exists</c>; externa <c>qep_soporte_respuesta</c>/<c>es</c> viva y
/// compatible ⇒ adopción (auditada con <c>["kind"]</c>), incompatible ⇒ <c>support_reply_conflict</c>; versión siguiente a la mayor
/// <c>qep_soporte_respuesta*</c> del espejo; hasta <see cref="MaxCreateAttempts"/> creaciones ante <c>name_taken</c>; crear antes de borrar
/// la rechazada; una sola transacción. La carrera entre dos primeros aprovisionamientos la cierra <c>UX_message_templates_support_reply</c>.
/// </summary>
public sealed partial class ProvisionSupportReplyTemplateHandler(
    IMessageTemplateRepository repository,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    CallerMembership callerMembership,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    ILogger<ProvisionSupportReplyTemplateHandler> logger,
    IValidator<ProvisionSupportReplyTemplateCommand> validator)
    : ICommandHandler<ProvisionSupportReplyTemplateCommand, ManagedTemplateDto>
{
    public const int MaxCreateAttempts = ProvisionQuotationTemplateHandler.MaxCreateAttempts;

    private static readonly string[] AdoptedFields = ["kind"];

    public async Task<ManagedTemplateDto> HandleAsync(ProvisionSupportReplyTemplateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var now = clock.UtcNow;
        var body = (command.Body ?? SupportReplyTemplateDefinition.BaseBody).Trim();

        var current = await repository.FindSupportReplyAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (current is { IsLive: true })
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateSupportReplyExists, "This connection already has its support reply template pending or approved.");
        }

        if (current is null
            && await repository.FindByNameAsync(
                command.TenantId, command.ConnectionId, SupportReplyTemplateDefinition.Name, SupportReplyTemplateDefinition.Language, cancellationToken)
                is { IsLive: true } external)
        {
            return await AdoptAsync(command, external, body, now, cancellationToken);
        }

        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var mirror = await repository.ListAsync(command.TenantId, command.ConnectionId, cancellationToken);
        var nextVersion = (mirror
            .Where(template => string.Equals(template.Language, SupportReplyTemplateDefinition.Language, StringComparison.Ordinal))
            .Select(template => SupportReplyTemplateDefinition.VersionOf(template.Name))
            .Max() ?? 0) + 1;

        var (created, name) = await CreateAsync(command, source, body, nextVersion, cancellationToken);

        if (current is not null)
        {
            var deleted = await meta.DeleteTemplateAsync(source, current.MetaTemplateId, current.Name, cancellationToken);
            if (deleted.Failure is { } failure)
            {
                if (TemplateGraphErrors.IsTokenRejected(failure))
                {
                    throw await TemplateGraphErrors.FailAsync(connections, command.TenantId, command.ConnectionId, failure, cancellationToken);
                }

                LogPreviousVersionNotDeleted(logger, command.TenantId, command.ConnectionId, current.MetaTemplateId);
            }

            repository.Remove(current);
        }

        var memberId = await callerMembership.FindAsync(command.TenantId, cancellationToken);
        var template = MessageTemplate.Pending(
            command.TenantId, command.ConnectionId, created.Id, name, SupportReplyTemplateDefinition.Language, created.Category,
            TemplateComponentsBuilder.Named, created.Status, TemplateComponentsBuilder.Components(SupportReplyTemplateDefinition.Draft(name, body)).ToJsonString(),
            MessageTemplateKind.SupportReply, memberId, now);
        repository.Add(template);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateSupportReplyProvisioned, template.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(template, await NamesOfAsync(command.TenantId, template, cancellationToken));
    }

    private async Task<ManagedTemplateDto> AdoptAsync(
        ProvisionSupportReplyTemplateCommand command, MessageTemplate external, string body, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!SupportReplyTemplateDefinition.IsAdoptable(external.ComponentsJson)
            || (command.Body is not null && !string.Equals(SupportReplyTemplateDefinition.StoredBody(external.ComponentsJson), body, StringComparison.Ordinal)))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateSupportReplyConflict,
                $"A template named '{SupportReplyTemplateDefinition.Name}' already exists in Meta with another body or an incompatible shape; delete it from the templates screen first.");
        }

        external.AdoptAsSupportReply(now);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateSupportReplyProvisioned, external.Id, now, AdoptedFields);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(external, await NamesOfAsync(command.TenantId, external, cancellationToken));
    }

    private async Task<(CreatedTemplate Created, string Name)> CreateAsync(
        ProvisionSupportReplyTemplateCommand command, MessagingTemplateSource source, string body, int firstVersion, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var name = SupportReplyTemplateDefinition.NameFor(firstVersion + attempt - 1);
            var created = await meta.CreateTemplateAsync(source, SupportReplyTemplateDefinition.Payload(name, body), cancellationToken);
            if (created.Succeeded && created.Value is { } value)
            {
                return (value, name);
            }

            var error = await TemplateGraphErrors.FailAsync(
                connections, command.TenantId, command.ConnectionId, created.Failure ?? new MessagingGraphFailure(0, null, "unreachable"), cancellationToken);
            if (error.Code != MessagingErrorCodes.TemplateNameTaken || attempt >= MaxCreateAttempts)
            {
                throw error;
            }
        }
    }

    private Task<IReadOnlyDictionary<Guid, string>> NamesOfAsync(Guid tenantId, MessageTemplate template, CancellationToken cancellationToken) =>
        TemplateManagementSupport.MemberNamesAsync(
            memberNames, tenantId, template.CreatedByMemberId is { } member ? [member] : [], cancellationToken);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Meta did not delete the previous support reply template {MetaTemplateId} of connection {ConnectionId} in tenant {TenantId}; its mirror row is removed anyway and the next refresh brings it back as External if Meta still has it.")]
    private static partial void LogPreviousVersionNotDeleted(ILogger logger, Guid tenantId, Guid connectionId, string metaTemplateId);
}
