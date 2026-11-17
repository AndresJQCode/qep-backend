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
/// Soporte fase 4b (H16, H18): la **única** fuente del nombre, idioma, categoría, texto base, ejemplos y botones de la plantilla de la encuesta
/// de satisfacción, calcada de <see cref="SupportReplyTemplateDefinition"/> sin encabezado. Sale por WhatsApp cuando un ticket queda resuelto.
/// El tenant edita el texto (Q-15) con las variables de <see cref="SystemTemplateVariables.SupportSurvey"/>, pero no los botones: Soporte lee
/// el toque por su texto (Meta lo devuelve como <c>button.text</c>, V-1), así que <see cref="ButtonLabels"/> es contrato. Categoría
/// <c>UTILITY</c>, sin <c>allow_category_change</c> (D-T10): Meta puede clasificarla como <c>MARKETING</c> (V-2). El nombre va por versiones
/// (<c>qep_soporte_encuesta</c>, <c>qep_soporte_encuesta_v2</c>…); el envío lee el nombre vigente de la fila.
/// </summary>
public static class SupportSurveyTemplateDefinition
{
    public const string Name = MessageTemplateName.SupportSurveyName;
    public const string Language = "es";
    public const string Category = "UTILITY";

    /// <summary>No empieza ni termina con una variable: Meta rechaza un cuerpo así.</summary>
    public const string BaseBody =
        "Hola {{nombre_contacto}}, tu caso {{radicado}} quedó resuelto. ¿Cómo calificas la atención que recibiste?";

    // La única clave vieja que el envío sabe llenar (LegacyKeys la traduce a nombre_contacto).
    private const string LegacyContactNameKey = "fullname";

    private const string QuickReply = "QuickReply";

    // «qep_soporte_encuesta_v»: dentro del prefijo reservado, así que ninguna libre puede tomar una versión.
    private const string VersionPrefix = MessageTemplateName.SupportSurveyPrefix + "v";

    /// <summary>Los tres botones de respuesta rápida, en este orden. Son fijos: el tenant no los edita y Soporte casa el toque con ellos.</summary>
    public static IReadOnlyList<string> ButtonLabels { get; } = ["Buena", "Regular", "Mala"];

    public static readonly IReadOnlyDictionary<string, string> Examples =
        SystemTemplateVariables.SupportSurvey.ToDictionary(variable => variable.Key, variable => variable.Example, StringComparer.Ordinal);

    /// <summary>El nombre de la versión <paramref name="version"/>: la 1 es <see cref="Name"/>; de la 2 en adelante, <c>qep_soporte_encuesta_v{n}</c>.</summary>
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

    /// <summary>Cuerpo y los tres botones fijos; sin encabezado ni pie.</summary>
    public static TemplateDraft Draft(string name, string body) =>
        new(name, Language, Category, null, body, null, ButtonLabels.Select(label => new TemplateButtonInput(QuickReply, label, null, null)).ToArray(),
            Examples);

    public static JsonObject Payload(string name, string body) => TemplateComponentsBuilder.CreatePayload(Draft(name, body));

    /// <summary>Una externa se adopta si no tiene encabezado, su cuerpo no está vacío, sus variables son de la lista de la encuesta o
    /// <c>fullname</c>, y tiene exactamente los tres botones de respuesta rápida de <see cref="ButtonLabels"/> en ese orden. Mira la forma, no
    /// el texto del cuerpo.</summary>
    public static bool IsAdoptable(string componentsJson)
    {
        ArgumentNullException.ThrowIfNull(componentsJson);
        var described = TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson);
        var allowed = SystemTemplateVariables.KeysOf(SystemTemplateVariables.SupportSurvey);
        return !string.IsNullOrWhiteSpace(described.Body)
            && described.Header is null
            && described.UnsupportedReason is null
            && described.Parameters.All(key => allowed.Contains(key) || string.Equals(key, LegacyContactNameKey, StringComparison.Ordinal))
            && described.Buttons.Count == ButtonLabels.Count
            && described.Buttons.Select(button => (button.Type, button.Text)).SequenceEqual(ButtonLabels.Select(label => (QuickReply, label)));
    }

    internal static string? StoredBody(string componentsJson) =>
        TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson).Body?.Trim();
}

/// <summary>Soporte fase 4b, <c>POST …/template-management/support-survey</c>: aprovisiona (o adopta, o reintenta) la plantilla de la
/// encuesta.</summary>
/// <param name="Body">El cuerpo editado, o <c>null</c> para el texto base. Los botones no se editan.</param>
public sealed record ProvisionSupportSurveyTemplateCommand(Guid TenantId, Guid ConnectionId, string? Body = null) : ICommand<ManagedTemplateDto>;

/// <summary>El cuerpo, si viene, usa sólo las variables de la encuesta.</summary>
public sealed class ProvisionSupportSurveyTemplateCommandValidator : AbstractValidator<ProvisionSupportSurveyTemplateCommand>
{
    public ProvisionSupportSurveyTemplateCommandValidator() =>
        RuleFor(command => command.Body).Custom((body, context) =>
        {
            if (body is not null && TemplateBodyRules.Check(body, SystemTemplateVariables.KeysOf(SystemTemplateVariables.SupportSurvey)) is { } error)
            {
                context.AddFailure(new ValidationFailure("body", error));
            }
        });
}

/// <summary>
/// Soporte fase 4b: el mismo procedimiento que <see cref="ProvisionSupportReplyTemplateHandler"/>, paso por paso, con la fila
/// <c>SupportSurvey</c> y los tres botones fijos: viva ⇒ <c>support_survey_exists</c>; externa <c>qep_soporte_encuesta</c>/<c>es</c> viva y
/// compatible ⇒ adopción (auditada con <c>["kind"]</c>), incompatible ⇒ <c>support_survey_conflict</c>; versión siguiente a la mayor
/// <c>qep_soporte_encuesta*</c> del espejo; hasta <see cref="MaxCreateAttempts"/> creaciones ante <c>name_taken</c>; crear antes de borrar
/// la rechazada; una sola transacción. La carrera entre dos primeros aprovisionamientos la cierra <c>UX_message_templates_support_survey</c>.
/// </summary>
public sealed partial class ProvisionSupportSurveyTemplateHandler(
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
    ILogger<ProvisionSupportSurveyTemplateHandler> logger,
    IValidator<ProvisionSupportSurveyTemplateCommand> validator)
    : ICommandHandler<ProvisionSupportSurveyTemplateCommand, ManagedTemplateDto>
{
    public const int MaxCreateAttempts = ProvisionQuotationTemplateHandler.MaxCreateAttempts;

    private static readonly string[] AdoptedFields = ["kind"];

    public async Task<ManagedTemplateDto> HandleAsync(ProvisionSupportSurveyTemplateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var now = clock.UtcNow;
        var body = (command.Body ?? SupportSurveyTemplateDefinition.BaseBody).Trim();

        var current = await repository.FindSupportSurveyAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (current is { IsLive: true })
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateSupportSurveyExists, "This connection already has its support survey template pending or approved.");
        }

        if (current is null
            && await repository.FindByNameAsync(
                command.TenantId, command.ConnectionId, SupportSurveyTemplateDefinition.Name, SupportSurveyTemplateDefinition.Language, cancellationToken)
                is { IsLive: true } external)
        {
            return await AdoptAsync(command, external, body, now, cancellationToken);
        }

        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var mirror = await repository.ListAsync(command.TenantId, command.ConnectionId, cancellationToken);
        var nextVersion = (mirror
            .Where(template => string.Equals(template.Language, SupportSurveyTemplateDefinition.Language, StringComparison.Ordinal))
            .Select(template => SupportSurveyTemplateDefinition.VersionOf(template.Name))
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
            command.TenantId, command.ConnectionId, created.Id, name, SupportSurveyTemplateDefinition.Language, created.Category,
            TemplateComponentsBuilder.Named, created.Status,
            TemplateComponentsBuilder.Components(SupportSurveyTemplateDefinition.Draft(name, body)).ToJsonString(),
            MessageTemplateKind.SupportSurvey, memberId, now);
        repository.Add(template);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateSupportSurveyProvisioned, template.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(template, await NamesOfAsync(command.TenantId, template, cancellationToken));
    }

    private async Task<ManagedTemplateDto> AdoptAsync(
        ProvisionSupportSurveyTemplateCommand command, MessageTemplate external, string body, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!SupportSurveyTemplateDefinition.IsAdoptable(external.ComponentsJson)
            || (command.Body is not null && !string.Equals(SupportSurveyTemplateDefinition.StoredBody(external.ComponentsJson), body, StringComparison.Ordinal)))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateSupportSurveyConflict,
                $"A template named '{SupportSurveyTemplateDefinition.Name}' already exists in Meta with another body or other buttons; delete it from the templates screen first.");
        }

        external.AdoptAsSupportSurvey(now);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateSupportSurveyProvisioned, external.Id, now, AdoptedFields);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(external, await NamesOfAsync(command.TenantId, external, cancellationToken));
    }

    private async Task<(CreatedTemplate Created, string Name)> CreateAsync(
        ProvisionSupportSurveyTemplateCommand command, MessagingTemplateSource source, string body, int firstVersion, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var name = SupportSurveyTemplateDefinition.NameFor(firstVersion + attempt - 1);
            var created = await meta.CreateTemplateAsync(source, SupportSurveyTemplateDefinition.Payload(name, body), cancellationToken);
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
        Message = "Meta did not delete the previous support survey template {MetaTemplateId} of connection {ConnectionId} in tenant {TenantId}; its mirror row is removed anyway and the next refresh brings it back as External if Meta still has it.")]
    private static partial void LogPreviousVersionNotDeleted(ILogger logger, Guid tenantId, Guid connectionId, string metaTemplateId);
}
