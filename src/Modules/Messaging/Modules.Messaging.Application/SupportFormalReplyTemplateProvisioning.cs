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
/// Soporte fase 4b (H10, owner 2026-11-16 Q-1): la **única** fuente del nombre, idioma, categoría, texto base y ejemplos de la plantilla
/// «Respuesta formal». Sale cuando la respuesta formal de un ticket va por WhatsApp fuera de la ventana de 24 h o sin conversación, con el
/// PDF como encabezado de documento. Es un tipo aparte de <see cref="SupportReplyTemplateDefinition"/> porque Meta no admite un encabezado
/// opcional: el camino del encabezado es el de <see cref="OrderTemplateDefinition"/> (PDF de ejemplo, <c>header_handle</c>). Las variables
/// son las de «Aviso de respuesta» (<see cref="SystemTemplateVariables.SupportFormalReply"/>) y el texto lo edita el tenant al crearla
/// (Q-15). El nombre va por versiones (<c>qep_soporte_formal</c>, <c>qep_soporte_formal_v2</c>…); el envío lee el nombre vigente de la fila.
/// </summary>
public static class SupportFormalReplyTemplateDefinition
{
    public const string Name = MessageTemplateName.SupportFormalReplyName;
    public const string Language = "es";
    public const string Category = "UTILITY";

    /// <summary>No empieza ni termina con una variable: Meta rechaza un cuerpo así. <c>{{respuesta}}</c> va como una oración propia,
    /// entre un punto y un espacio: el texto del asesor suele terminar en punto, y un signo pegado a la variable daría «..».</summary>
    public const string BaseBody =
        "Hola {{nombre_contacto}}, adjuntamos la respuesta formal a tu caso {{radicado}}. {{respuesta}} Si tienes dudas, responde este mensaje.";

    public const string ExampleFileName = "respuesta-formal-ejemplo.pdf";
    public const string ExampleMimeType = "application/pdf";

    // La única clave vieja que el envío sabe llenar (LegacyKeys la traduce a nombre_contacto).
    private const string LegacyContactNameKey = "fullname";

    // «qep_soporte_formal_v»: dentro del prefijo reservado, así que ninguna libre puede tomar una versión.
    private const string VersionPrefix = MessageTemplateName.SupportFormalReplyPrefix + "v";

    public static readonly IReadOnlyDictionary<string, string> Examples =
        SystemTemplateVariables.SupportFormalReply.ToDictionary(variable => variable.Key, variable => variable.Example, StringComparer.Ordinal);

    /// <summary>El nombre de la versión <paramref name="version"/>: la 1 es <see cref="Name"/>; de la 2 en adelante, <c>qep_soporte_formal_v{n}</c>.</summary>
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

    /// <summary>Sin pie ni botones; el encabezado de documento lo pone el handle al armar el payload.</summary>
    public static TemplateDraft Draft(string name, string body) => new(name, Language, Category, null, body, null, [], Examples);

    public static JsonObject Payload(string handle, string name, string body) => TemplateComponentsBuilder.CreatePayload(Draft(name, body), TemplateMediaHeader.Document(handle));

    /// <summary>Una externa se adopta si tiene encabezado de documento, cuerpo no vacío, ningún botón y sus variables son de la lista de
    /// Soporte o <c>fullname</c> (la única clave vieja que el envío sabe llenar). Mira la forma, no el texto.</summary>
    public static bool IsAdoptable(string componentsJson)
    {
        ArgumentNullException.ThrowIfNull(componentsJson);
        var described = TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson);
        var allowed = SystemTemplateVariables.KeysOf(SystemTemplateVariables.SupportFormalReply);
        return !string.IsNullOrWhiteSpace(described.Body)
            && described.Header is null
            && described.UnsupportedReason == TemplateUnsupportedReasons.MediaHeader
            && described.Parameters.All(key => allowed.Contains(key) || string.Equals(key, LegacyContactNameKey, StringComparison.Ordinal))
            && described.Buttons.Count == 0
            && OrderTemplateDefinition.IsDocumentHeader(componentsJson);
    }

    internal static string? StoredBody(string componentsJson) =>
        TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson).Body?.Trim();

    public static byte[] ExamplePdf() => MinimalPdf.Build("Respuesta formal de ejemplo - QEP");
}

/// <summary>Soporte fase 4b, <c>POST …/template-management/support-formal-reply</c>: aprovisiona (o adopta, o reintenta) la plantilla
/// «Respuesta formal».</summary>
/// <param name="Body">El cuerpo editado, o <c>null</c> para el texto base.</param>
public sealed record ProvisionSupportFormalReplyTemplateCommand(Guid TenantId, Guid ConnectionId, string? Body = null) : ICommand<ManagedTemplateDto>;

/// <summary>El cuerpo, si viene, usa sólo las variables de Soporte.</summary>
public sealed class ProvisionSupportFormalReplyTemplateCommandValidator : AbstractValidator<ProvisionSupportFormalReplyTemplateCommand>
{
    public ProvisionSupportFormalReplyTemplateCommandValidator() =>
        RuleFor(command => command.Body).Custom((body, context) =>
        {
            if (body is not null
                && TemplateBodyRules.Check(body, SystemTemplateVariables.KeysOf(SystemTemplateVariables.SupportFormalReply)) is { } error)
            {
                context.AddFailure(new ValidationFailure("body", error));
            }
        });
}

/// <summary>
/// Soporte fase 4b: el mismo procedimiento que <see cref="ProvisionOrderTemplateHandler"/>, paso por paso, con la fila
/// <c>SupportFormalReply</c>: viva ⇒ <c>support_formal_reply_exists</c>; externa <c>qep_soporte_formal</c>/<c>es</c> viva y compatible ⇒
/// adopción (auditada con <c>["kind"]</c>), incompatible ⇒ <c>support_formal_reply_conflict</c>; versión siguiente a la mayor
/// <c>qep_soporte_formal*</c> del espejo; una subida del PDF de ejemplo y hasta <see cref="MaxCreateAttempts"/> creaciones ante
/// <c>name_taken</c>; crear antes de borrar la rechazada; una sola transacción. La carrera entre dos primeros aprovisionamientos la cierra
/// <c>UX_message_templates_support_formal_reply</c>.
/// </summary>
public sealed partial class ProvisionSupportFormalReplyTemplateHandler(
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
    ILogger<ProvisionSupportFormalReplyTemplateHandler> logger,
    IValidator<ProvisionSupportFormalReplyTemplateCommand> validator)
    : ICommandHandler<ProvisionSupportFormalReplyTemplateCommand, ManagedTemplateDto>
{
    public const int MaxCreateAttempts = ProvisionQuotationTemplateHandler.MaxCreateAttempts;

    private static readonly string[] AdoptedFields = ["kind"];

    public async Task<ManagedTemplateDto> HandleAsync(ProvisionSupportFormalReplyTemplateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var now = clock.UtcNow;
        var body = (command.Body ?? SupportFormalReplyTemplateDefinition.BaseBody).Trim();

        var current = await repository.FindSupportFormalReplyAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (current is { IsLive: true })
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateSupportFormalReplyExists, "This connection already has its support formal reply template pending or approved.");
        }

        if (current is null
            && await repository.FindByNameAsync(
                command.TenantId, command.ConnectionId, SupportFormalReplyTemplateDefinition.Name, SupportFormalReplyTemplateDefinition.Language,
                cancellationToken) is { IsLive: true } external)
        {
            return await AdoptAsync(command, external, body, now, cancellationToken);
        }

        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var mirror = await repository.ListAsync(command.TenantId, command.ConnectionId, cancellationToken);
        var nextVersion = (mirror
            .Where(template => string.Equals(template.Language, SupportFormalReplyTemplateDefinition.Language, StringComparison.Ordinal))
            .Select(template => SupportFormalReplyTemplateDefinition.VersionOf(template.Name))
            .Max() ?? 0) + 1;

        var handle = await UploadExampleAsync(command, source, cancellationToken);
        var (created, name) = await CreateAsync(command, source, handle, body, nextVersion, cancellationToken);

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
            command.TenantId, command.ConnectionId, created.Id, name, SupportFormalReplyTemplateDefinition.Language, created.Category,
            TemplateComponentsBuilder.Named, created.Status,
            TemplateComponentsBuilder.Components(SupportFormalReplyTemplateDefinition.Draft(name, body), TemplateMediaHeader.Document(handle)).ToJsonString(),
            MessageTemplateKind.SupportFormalReply, memberId, now);
        repository.Add(template);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateSupportFormalReplyProvisioned, template.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(template, await NamesOfAsync(command.TenantId, template, cancellationToken));
    }

    private async Task<ManagedTemplateDto> AdoptAsync(
        ProvisionSupportFormalReplyTemplateCommand command, MessageTemplate external, string body, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!SupportFormalReplyTemplateDefinition.IsAdoptable(external.ComponentsJson)
            || (command.Body is not null
                && !string.Equals(SupportFormalReplyTemplateDefinition.StoredBody(external.ComponentsJson), body, StringComparison.Ordinal)))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateSupportFormalReplyConflict,
                $"A template named '{SupportFormalReplyTemplateDefinition.Name}' already exists in Meta with another body or an incompatible shape; delete it from the templates screen first.");
        }

        external.AdoptAsSupportFormalReply(now);
        audit.RecordTemplate(
            command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateSupportFormalReplyProvisioned, external.Id, now, AdoptedFields);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(external, await NamesOfAsync(command.TenantId, external, cancellationToken));
    }

    private async Task<string> UploadExampleAsync(
        ProvisionSupportFormalReplyTemplateCommand command, MessagingTemplateSource source, CancellationToken cancellationToken)
    {
        var upload = await meta.UploadTemplateExampleAsync(
            source, SupportFormalReplyTemplateDefinition.ExamplePdf(), SupportFormalReplyTemplateDefinition.ExampleMimeType,
            SupportFormalReplyTemplateDefinition.ExampleFileName, cancellationToken);
        if (upload.Succeeded && upload.Value is { } handle)
        {
            return handle;
        }

        // Como en la de pedido: la subida es un endpoint de la app, no de la WABA; nunca se reporta la conexión.
        var failure = upload.Failure ?? new MessagingGraphFailure(0, null, "no_handle");
        LogUploadFailed(
            logger, command.TenantId, command.ConnectionId, failure.HttpStatus, failure.Code?.ToString(CultureInfo.InvariantCulture) ?? "none", failure.Reason);
        throw new MessagingDomainException(MessagingErrorCodes.TemplateUploadFailed, "WhatsApp did not accept the example PDF of the support formal reply template.");
    }

    private async Task<(CreatedTemplate Created, string Name)> CreateAsync(
        ProvisionSupportFormalReplyTemplateCommand command, MessagingTemplateSource source, string handle, string body, int firstVersion,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var name = SupportFormalReplyTemplateDefinition.NameFor(firstVersion + attempt - 1);
            var created = await meta.CreateTemplateAsync(source, SupportFormalReplyTemplateDefinition.Payload(handle, name, body), cancellationToken);
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
        Message = "Meta did not delete the previous support formal reply template {MetaTemplateId} of connection {ConnectionId} in tenant {TenantId}; its mirror row is removed anyway and the next refresh brings it back as External if Meta still has it.")]
    private static partial void LogPreviousVersionNotDeleted(ILogger logger, Guid tenantId, Guid connectionId, string metaTemplateId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Meta did not accept the example PDF of the support formal reply template for connection {ConnectionId} in tenant {TenantId}: status {HttpStatus}, code {GraphCode}, reason {Reason}; the connection is not reported.")]
    private static partial void LogUploadFailed(ILogger logger, Guid tenantId, Guid connectionId, int httpStatus, string graphCode, string reason);
}
