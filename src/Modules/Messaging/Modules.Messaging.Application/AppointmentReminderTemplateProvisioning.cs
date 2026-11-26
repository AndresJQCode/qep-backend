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
/// Agenda: la **única** fuente del nombre, idioma, categoría, texto base y ejemplos de la plantilla del recordatorio de cita por WhatsApp,
/// calcada de <see cref="WholesaleInvitationNoticeTemplateDefinition"/>: sólo cuerpo, sin encabezado ni botones. El recordatorio dice quién,
/// qué servicio, cuándo, con quién y dónde es la cita; nada más viaja. El admin puede editar el texto al crearla, con las variables de
/// <see cref="SystemTemplateVariables.AppointmentReminder"/>. El nombre va por versiones (<c>qep_agenda_recordatorio</c>,
/// <c>qep_agenda_recordatorio_v2</c>…) porque Meta bloquea un tiempo el nombre de una plantilla borrada. El envío lee el nombre vigente de la
/// fila <c>AppointmentReminder</c>, no de esta constante.
/// </summary>
public static class AppointmentReminderTemplateDefinition
{
    public const string Name = MessageTemplateName.AppointmentReminderName;
    public const string Language = "es";
    public const string Category = "UTILITY";

    /// <summary>El texto aprobado por el owner. No empieza ni termina con una variable: Meta rechaza un cuerpo así.</summary>
    public const string BaseBody =
        "¡Hola, {{nombre_cliente}}! Te recordamos tu cita de {{servicio}} el {{fecha_hora}} con {{recurso}} en {{sede}}. "
        + "Si no puedes asistir, avísanos con tiempo para reprogramarla.";

    /// <summary>Las claves que el envío llena además de las fijas: el nombre del cliente, el servicio, la fecha y hora en la zona del tenant,
    /// el recurso (la persona o el espacio) y la sede.</summary>
    public const string ClientKey = "nombre_cliente";

    public const string ServiceKey = "servicio";

    public const string DateTimeKey = "fecha_hora";

    public const string ResourceKey = "recurso";

    public const string LocationKey = "sede";

    // «qep_agenda_recordatorio_v»: dentro del prefijo reservado, así que ninguna libre puede tomar una versión.
    private const string VersionPrefix = MessageTemplateName.AppointmentReminderPrefix + "v";

    public static readonly IReadOnlyDictionary<string, string> Examples =
        SystemTemplateVariables.AppointmentReminder.ToDictionary(variable => variable.Key, variable => variable.Example, StringComparer.Ordinal);

    /// <summary>El nombre de la versión <paramref name="version"/>: la 1 es <see cref="Name"/>; de la 2 en adelante, <c>qep_agenda_recordatorio_v{n}</c>.</summary>
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

    /// <summary>Sólo cuerpo: sin encabezado, pie ni botones.</summary>
    public static TemplateDraft Draft(string name, string body) => new(name, Language, Category, null, body, null, [], Examples);

    public static JsonObject Payload(string name, string body) => TemplateComponentsBuilder.CreatePayload(Draft(name, body));

    /// <summary>Una externa se adopta si tiene sólo cuerpo (sin encabezado de ningún tipo ni botones), no vacío, y sus variables son todas
    /// de la lista del recordatorio. A diferencia de Soporte no se admite ninguna clave vieja: esta plantilla nació con las nuevas y el envío no
    /// sabe llenar otra. Mira la forma, no el texto.</summary>
    public static bool IsAdoptable(string componentsJson)
    {
        ArgumentNullException.ThrowIfNull(componentsJson);
        var described = TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson);
        var allowed = SystemTemplateVariables.KeysOf(SystemTemplateVariables.AppointmentReminder);
        return !string.IsNullOrWhiteSpace(described.Body)
            && described.Header is null
            && described.UnsupportedReason is null
            && described.Parameters.All(allowed.Contains)
            && described.Buttons.Count == 0;
    }

    internal static string? StoredBody(string componentsJson) =>
        TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson).Body?.Trim();
}

/// <summary>Agenda, <c>POST …/template-management/appointment-reminder</c>: aprovisiona (o adopta, o reintenta) la plantilla del
/// recordatorio de cita.</summary>
/// <param name="Body">El cuerpo editado, o <c>null</c> para el texto base.</param>
public sealed record ProvisionAppointmentReminderTemplateCommand(Guid TenantId, Guid ConnectionId, string? Body = null) : ICommand<ManagedTemplateDto>;

/// <summary>El cuerpo, si viene, usa sólo las variables del recordatorio.</summary>
public sealed class ProvisionAppointmentReminderTemplateCommandValidator : AbstractValidator<ProvisionAppointmentReminderTemplateCommand>
{
    public ProvisionAppointmentReminderTemplateCommandValidator() =>
        RuleFor(command => command.Body).Custom((body, context) =>
        {
            if (body is not null
                && TemplateBodyRules.Check(body, SystemTemplateVariables.KeysOf(SystemTemplateVariables.AppointmentReminder)) is { } error)
            {
                context.AddFailure(new ValidationFailure("body", error));
            }
        });
}

/// <summary>
/// Agenda: el mismo procedimiento que <see cref="ProvisionSupportReplyTemplateHandler"/>, paso por paso, con la fila
/// <c>AppointmentReminder</c>: viva ⇒ <c>appointment_reminder_exists</c>; externa <c>qep_agenda_recordatorio</c>/<c>es</c> viva y
/// compatible ⇒ adopción (auditada con <c>["kind"]</c>), incompatible ⇒ <c>appointment_reminder_conflict</c>; versión siguiente a la
/// mayor <c>qep_agenda_recordatorio*</c> del espejo; hasta <see cref="MaxCreateAttempts"/> creaciones ante <c>name_taken</c>; crear antes de
/// borrar la rechazada; una sola transacción. La carrera entre dos primeros aprovisionamientos la cierra
/// <c>UX_message_templates_appointment_reminder</c>. Sin gate de módulo, como la de pedido.
/// </summary>
public sealed partial class ProvisionAppointmentReminderTemplateHandler(
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
    ILogger<ProvisionAppointmentReminderTemplateHandler> logger,
    IValidator<ProvisionAppointmentReminderTemplateCommand> validator)
    : ICommandHandler<ProvisionAppointmentReminderTemplateCommand, ManagedTemplateDto>
{
    public const int MaxCreateAttempts = ProvisionQuotationTemplateHandler.MaxCreateAttempts;

    private static readonly string[] AdoptedFields = ["kind"];

    public async Task<ManagedTemplateDto> HandleAsync(ProvisionAppointmentReminderTemplateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var now = clock.UtcNow;
        var body = (command.Body ?? AppointmentReminderTemplateDefinition.BaseBody).Trim();

        var current = await repository.FindAppointmentReminderAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (current is { IsLive: true })
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateAppointmentReminderExists,
                "This connection already has its appointment reminder template pending or approved.");
        }

        if (current is null
            && await repository.FindByNameAsync(
                command.TenantId, command.ConnectionId, AppointmentReminderTemplateDefinition.Name, AppointmentReminderTemplateDefinition.Language,
                cancellationToken)
                is { IsLive: true } external)
        {
            return await AdoptAsync(command, external, body, now, cancellationToken);
        }

        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var mirror = await repository.ListAsync(command.TenantId, command.ConnectionId, cancellationToken);
        var nextVersion = (mirror
            .Where(template => string.Equals(template.Language, AppointmentReminderTemplateDefinition.Language, StringComparison.Ordinal))
            .Select(template => AppointmentReminderTemplateDefinition.VersionOf(template.Name))
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
            command.TenantId, command.ConnectionId, created.Id, name, AppointmentReminderTemplateDefinition.Language, created.Category,
            TemplateComponentsBuilder.Named, created.Status,
            TemplateComponentsBuilder.Components(AppointmentReminderTemplateDefinition.Draft(name, body)).ToJsonString(),
            MessageTemplateKind.AppointmentReminder, memberId, now);
        repository.Add(template);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateAppointmentReminderProvisioned, template.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(template, await NamesOfAsync(command.TenantId, template, cancellationToken));
    }

    private async Task<ManagedTemplateDto> AdoptAsync(
        ProvisionAppointmentReminderTemplateCommand command, MessageTemplate external, string body, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!AppointmentReminderTemplateDefinition.IsAdoptable(external.ComponentsJson)
            || (command.Body is not null
                && !string.Equals(AppointmentReminderTemplateDefinition.StoredBody(external.ComponentsJson), body, StringComparison.Ordinal)))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateAppointmentReminderConflict,
                $"A template named '{AppointmentReminderTemplateDefinition.Name}' already exists in Meta with another body or an incompatible shape; delete it from the templates screen first.");
        }

        external.AdoptAsAppointmentReminder(now);
        audit.RecordTemplate(
            command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateAppointmentReminderProvisioned, external.Id, now, AdoptedFields);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(external, await NamesOfAsync(command.TenantId, external, cancellationToken));
    }

    private async Task<(CreatedTemplate Created, string Name)> CreateAsync(
        ProvisionAppointmentReminderTemplateCommand command, MessagingTemplateSource source, string body, int firstVersion,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var name = AppointmentReminderTemplateDefinition.NameFor(firstVersion + attempt - 1);
            var created = await meta.CreateTemplateAsync(source, AppointmentReminderTemplateDefinition.Payload(name, body), cancellationToken);
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
        Message = "Meta did not delete the previous appointment reminder template {MetaTemplateId} of connection {ConnectionId} in tenant {TenantId}; its mirror row is removed anyway and the next refresh brings it back as External if Meta still has it.")]
    private static partial void LogPreviousVersionNotDeleted(ILogger logger, Guid tenantId, Guid connectionId, string metaTemplateId);
}
