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
/// Support task notices: the **only** source of the name, language, category, base text and examples of the WhatsApp template that tells a
/// member a ticket or internal task was assigned to them. Body only, no header or buttons. It serves both internal tasks and customer
/// tickets, so the text never says "tarea". It never carries the subject or the description. The admin may edit the text when creating it,
/// with the variables of <see cref="SystemTemplateVariables.TaskNotice"/>. The name is versioned (<c>qep_tarea_asignada</c>,
/// <c>qep_tarea_asignada_v2</c>…) because Meta blocks a deleted template's name for a while. Sending reads the current name from the
/// <c>TaskNotice</c> row, not from this constant.
/// </summary>
public static class TaskNoticeTemplateDefinition
{
    public const string Name = MessageTemplateName.TaskNoticeName;
    public const string Language = "es";
    public const string Category = "UTILITY";

    /// <summary>Owner-approved text (2026-11-22). It neither starts nor ends with a variable: Meta rejects such a body.</summary>
    public const string BaseBody = "¡Hola! {{asesor}} te asignó {{numero}} en QEP. Ábrelo para ver el detalle.";

    /// <summary>The key the sender fills besides the fixed ones: the ticket number.</summary>
    public const string NumberKey = "numero";

    // «qep_tarea_asignada_v»: dentro del prefijo reservado, así que ninguna libre puede tomar una versión.
    private const string VersionPrefix = MessageTemplateName.TaskNoticePrefix + "v";

    public static readonly IReadOnlyDictionary<string, string> Examples =
        SystemTemplateVariables.TaskNotice.ToDictionary(variable => variable.Key, variable => variable.Example, StringComparer.Ordinal);

    /// <summary>El nombre de la versión <paramref name="version"/>: la 1 es <see cref="Name"/>; de la 2 en adelante, <c>qep_tarea_asignada_v{n}</c>.</summary>
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
    /// de la lista del aviso. A diferencia de Soporte no se admite ninguna clave vieja: esta plantilla nació con las nuevas y el envío no
    /// sabe llenar otra. Mira la forma, no el texto.</summary>
    public static bool IsAdoptable(string componentsJson)
    {
        ArgumentNullException.ThrowIfNull(componentsJson);
        var described = TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson);
        var allowed = SystemTemplateVariables.KeysOf(SystemTemplateVariables.TaskNotice);
        return !string.IsNullOrWhiteSpace(described.Body)
            && described.Header is null
            && described.UnsupportedReason is null
            && described.Parameters.All(allowed.Contains)
            && described.Buttons.Count == 0;
    }

    internal static string? StoredBody(string componentsJson) =>
        TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson).Body?.Trim();
}

/// <summary>Support task notices, <c>POST …/template-management/task-notice</c>: provisions (or adopts, or retries) the template of the
/// assignment notice.</summary>
/// <param name="Body">El cuerpo editado, o <c>null</c> para el texto base.</param>
public sealed record ProvisionTaskNoticeTemplateCommand(Guid TenantId, Guid ConnectionId, string? Body = null) : ICommand<ManagedTemplateDto>;

/// <summary>El cuerpo, si viene, usa sólo las variables del aviso.</summary>
public sealed class ProvisionTaskNoticeTemplateCommandValidator : AbstractValidator<ProvisionTaskNoticeTemplateCommand>
{
    public ProvisionTaskNoticeTemplateCommandValidator() =>
        RuleFor(command => command.Body).Custom((body, context) =>
        {
            if (body is not null
                && TemplateBodyRules.Check(body, SystemTemplateVariables.KeysOf(SystemTemplateVariables.TaskNotice)) is { } error)
            {
                context.AddFailure(new ValidationFailure("body", error));
            }
        });
}

/// <summary>
/// Support task notices: the same procedure as <see cref="ProvisionSupportReplyTemplateHandler"/>, step by step, with the <c>TaskNotice</c> row:
/// live ⇒ <c>task_notice_exists</c>; a live, compatible external <c>qep_tarea_asignada</c>/<c>es</c> ⇒ adoption (audited with <c>["kind"]</c>),
/// incompatible ⇒ <c>task_notice_conflict</c>; next version after the highest <c>qep_tarea_asignada*</c> in the mirror; up to
/// <see cref="MaxCreateAttempts"/> creations on <c>name_taken</c>; create before deleting the rejected one; one transaction. The race between
/// two first provisionings is closed by <c>UX_message_templates_task_notice</c>. No module gate, like the order template.
/// </summary>
public sealed partial class ProvisionTaskNoticeTemplateHandler(
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
    ILogger<ProvisionTaskNoticeTemplateHandler> logger,
    IValidator<ProvisionTaskNoticeTemplateCommand> validator)
    : ICommandHandler<ProvisionTaskNoticeTemplateCommand, ManagedTemplateDto>
{
    public const int MaxCreateAttempts = ProvisionQuotationTemplateHandler.MaxCreateAttempts;

    private static readonly string[] AdoptedFields = ["kind"];

    public async Task<ManagedTemplateDto> HandleAsync(ProvisionTaskNoticeTemplateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var now = clock.UtcNow;
        var body = (command.Body ?? TaskNoticeTemplateDefinition.BaseBody).Trim();

        var current = await repository.FindTaskNoticeAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (current is { IsLive: true })
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateTaskNoticeExists,
                "This connection already has its task notice template pending or approved.");
        }

        if (current is null
            && await repository.FindByNameAsync(
                command.TenantId, command.ConnectionId, TaskNoticeTemplateDefinition.Name, TaskNoticeTemplateDefinition.Language,
                cancellationToken)
                is { IsLive: true } external)
        {
            return await AdoptAsync(command, external, body, now, cancellationToken);
        }

        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var mirror = await repository.ListAsync(command.TenantId, command.ConnectionId, cancellationToken);
        var nextVersion = (mirror
            .Where(template => string.Equals(template.Language, TaskNoticeTemplateDefinition.Language, StringComparison.Ordinal))
            .Select(template => TaskNoticeTemplateDefinition.VersionOf(template.Name))
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
            command.TenantId, command.ConnectionId, created.Id, name, TaskNoticeTemplateDefinition.Language, created.Category,
            TemplateComponentsBuilder.Named, created.Status,
            TemplateComponentsBuilder.Components(TaskNoticeTemplateDefinition.Draft(name, body)).ToJsonString(),
            MessageTemplateKind.TaskNotice, memberId, now);
        repository.Add(template);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateTaskNoticeProvisioned, template.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(template, await NamesOfAsync(command.TenantId, template, cancellationToken));
    }

    private async Task<ManagedTemplateDto> AdoptAsync(
        ProvisionTaskNoticeTemplateCommand command, MessageTemplate external, string body, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!TaskNoticeTemplateDefinition.IsAdoptable(external.ComponentsJson)
            || (command.Body is not null
                && !string.Equals(TaskNoticeTemplateDefinition.StoredBody(external.ComponentsJson), body, StringComparison.Ordinal)))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateTaskNoticeConflict,
                $"A template named '{TaskNoticeTemplateDefinition.Name}' already exists in Meta with another body or an incompatible shape; delete it from the templates screen first.");
        }

        external.AdoptAsTaskNotice(now);
        audit.RecordTemplate(
            command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateTaskNoticeProvisioned, external.Id, now, AdoptedFields);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(external, await NamesOfAsync(command.TenantId, external, cancellationToken));
    }

    private async Task<(CreatedTemplate Created, string Name)> CreateAsync(
        ProvisionTaskNoticeTemplateCommand command, MessagingTemplateSource source, string body, int firstVersion,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var name = TaskNoticeTemplateDefinition.NameFor(firstVersion + attempt - 1);
            var created = await meta.CreateTemplateAsync(source, TaskNoticeTemplateDefinition.Payload(name, body), cancellationToken);
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
        Message = "Meta did not delete the previous task notice template {MetaTemplateId} of connection {ConnectionId} in tenant {TenantId}; its mirror row is removed anyway and the next refresh brings it back as External if Meta still has it.")]
    private static partial void LogPreviousVersionNotDeleted(ILogger logger, Guid tenantId, Guid connectionId, string metaTemplateId);
}
