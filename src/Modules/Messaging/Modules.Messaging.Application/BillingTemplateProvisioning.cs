using System.Globalization;
using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Cobros recurrentes §5 (plantillas aprobadas por el owner, 2026-10-29) y plantillas editables: una de las dos plantillas de cobro.
/// <c>BaseBody</c> es el texto del owner, el que se crea si el admin no manda otro; <c>Label</c> es cómo se nombra el aviso al decir cuál
/// falló (<c>Recordatorio</c> o <c>Vencido</c>). Las variables permitidas y sus ejemplos salen de <see cref="Variables"/>; sin encabezado ni
/// pie. <c>LegacyPositionalKeys</c> dice qué valor era cada <c>{{1}}</c>, <c>{{2}}</c>…: una fila vieja POSITIONAL se sigue enviando en ese
/// orden.
/// </summary>
public sealed record BillingTemplateDefinition(
    MessageTemplateKind Kind, string Name, string Label, string BaseBody, IReadOnlyList<string> LegacyPositionalKeys)
{
    private string VersionPrefix => Name + "_v";

    /// <summary>La lista cerrada de variables de este aviso: otra, incluida la del otro aviso, no pasa.</summary>
    public IReadOnlyList<SystemTemplateVariable> Variables => Kind == MessageTemplateKind.BillingReminder
        ? SystemTemplateVariables.BillingReminder
        : SystemTemplateVariables.BillingOverdue;

    /// <summary>El nombre de la versión <paramref name="version"/>: la 1 es <see cref="Name"/>; de la 2 en adelante, <c>{Name}_v{n}</c>. Las usa
    /// el reintento, por la misma razón que en la cotización: Meta bloquea un tiempo el nombre de una plantilla borrada.</summary>
    public string NameFor(int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return version == 1 ? Name : VersionPrefix + version.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>El inverso de <see cref="NameFor"/>: 1 para <see cref="Name"/>, n para <c>{Name}_v{n}</c> con n ≥ 2 sin ceros a la izquierda;
    /// cualquier otro nombre es <c>null</c>.</summary>
    public int? VersionOf(string? name)
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

    /// <summary>El borrador de la versión <paramref name="name"/> con el cuerpo <paramref name="body"/>: sólo cuerpo, con los ejemplos de
    /// <see cref="Variables"/>.</summary>
    public TemplateDraft Draft(string name, string body) => new(
        name, BillingTemplateDefinitions.Language, BillingTemplateDefinitions.Category, null, body, null, [],
        Variables.ToDictionary(variable => variable.Key, variable => variable.Example, StringComparer.Ordinal));

    /// <summary>El payload de creación de la versión <paramref name="name"/> con el cuerpo <paramref name="body"/>, con variables con nombre.</summary>
    public JsonObject Payload(string name, string body) => TemplateComponentsBuilder.CreatePayload(Draft(name, body));
}

/// <summary>Cobros recurrentes §5: la **única** fuente del nombre, idioma, categoría y texto base de las dos plantillas de cobro. Texto
/// literal del owner; el admin puede editarlo al crearlas.</summary>
public static class BillingTemplateDefinitions
{
    public const string Language = "es";
    public const string Category = "UTILITY";

    public static readonly BillingTemplateDefinition Reminder = new(
        MessageTemplateKind.BillingReminder,
        "qep_cobro_recordatorio",
        "Recordatorio",
        "Hola {{nombre_contacto}}, te recordamos que tu servicio {{servicio}} por {{valor}} vence el {{vencimiento}}. Si ya realizaste el pago, ignora este mensaje.",
        ["nombre_contacto", "servicio", "valor", "vencimiento"]);

    public static readonly BillingTemplateDefinition Overdue = new(
        MessageTemplateKind.BillingOverdue,
        "qep_cobro_vencido",
        "Vencido",
        "Hola {{nombre_contacto}}, tienes {{cantidad_cobros}} cobro(s) vencido(s) por un total de {{total_vencido}}. Escríbenos para ponerte al día. Si ya pagaste, ignora este mensaje.",
        ["nombre_contacto", "cantidad_cobros", "total_vencido"]);

    public static readonly IReadOnlyList<BillingTemplateDefinition> All = [Reminder, Overdue];

    public static BillingTemplateDefinition For(MessageTemplateKind kind) =>
        All.FirstOrDefault(definition => definition.Kind == kind)
        ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a billing template kind.");
}

/// <summary>Cobros recurrentes §5, <c>POST …/template-management/billing</c>: aprovisiona (o reintenta) las dos plantillas de cobro de la conexión.</summary>
/// <param name="ReminderBody">El cuerpo editado del recordatorio, o <c>null</c> para el texto base.</param>
/// <param name="OverdueBody">El cuerpo editado del aviso de vencidos, o <c>null</c> para el texto base.</param>
public sealed record ProvisionBillingTemplatesCommand(Guid TenantId, Guid ConnectionId, string? ReminderBody = null, string? OverdueBody = null)
    : ICommand<IReadOnlyList<ManagedTemplateDto>>;

/// <summary>Cada cuerpo, si viene, usa sólo las variables de su aviso: la del otro no pasa. Con <c>ValidationFailure</c> a mano, como el de la
/// cotización: los mensajes llevan <c>{{nombre}}</c> literal y <c>WithMessage</c> lo trataría como placeholder.</summary>
public sealed class ProvisionBillingTemplatesCommandValidator : AbstractValidator<ProvisionBillingTemplatesCommand>
{
    public ProvisionBillingTemplatesCommandValidator()
    {
        RuleFor(command => command.ReminderBody).Custom((body, context) => Check(body, BillingTemplateDefinitions.Reminder, "reminderBody", context));
        RuleFor(command => command.OverdueBody).Custom((body, context) => Check(body, BillingTemplateDefinitions.Overdue, "overdueBody", context));
    }

    private static void Check(string? body, BillingTemplateDefinition definition, string key, ValidationContext<ProvisionBillingTemplatesCommand> context)
    {
        if (body is not null && TemplateBodyRules.Check(body, SystemTemplateVariables.KeysOf(definition.Variables)) is { } error)
        {
            context.AddFailure(new ValidationFailure(key, error));
        }
    }
}

/// <summary>
/// Cobros recurrentes §5: el aprovisionamiento de <see cref="QuotationTemplateDefinition"/> sin encabezado de documento ni adopción, para las
/// dos plantillas de cobro, con el cuerpo editado de cada una o su texto base. Valida los dos cuerpos antes de tocar Meta y, por cada
/// definición, en orden:
/// <list type="number">
/// <item>Si la conexión ya tiene su fila de ese tipo viva (pendiente, aprobada o en apelación), no se toca: es idempotente y su cuerpo se ignora.</item>
/// <item>El nombre a crear es la versión siguiente a la mayor del espejo para ese nombre base (cualquier kind, idioma <c>es</c>); sin ninguna, la 1.
/// Si Meta dice que el nombre ya existe (uno en cuarentena o creado a mano que el espejo no conoce), prueba la siguiente hasta
/// <see cref="MaxCreateAttempts"/> creaciones.</item>
/// <item>Con la nueva creada, borra en Meta la fila vieja del tipo (rechazada o pausada) y la quita del espejo: crear antes de borrar deja la
/// vieja intacta si la creación falla.</item>
/// <item>Guarda esa plantilla (fila, auditoría y la vieja quitada) antes de pasar a la siguiente.</item>
/// </list>
/// Se guarda una plantilla a la vez porque cada una puede ser rechazada por Meta por separado: si la segunda falla, la primera ya quedó en
/// Meta y en el espejo, y reintentar crea sólo la segunda. Un rechazo de Meta lleva por delante el aviso que falló (<c>Vencido: …</c>); otro
/// error conserva su código y su mensaje. El índice único parcial <c>UX_message_templates_billing</c> cierra la carrera.
/// </summary>
public sealed partial class ProvisionBillingTemplatesHandler(
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
    ILogger<ProvisionBillingTemplatesHandler> logger,
    IValidator<ProvisionBillingTemplatesCommand> validator)
    : ICommandHandler<ProvisionBillingTemplatesCommand, IReadOnlyList<ManagedTemplateDto>>
{
    /// <summary>Creaciones por plantilla, contando la primera: un tope para no recorrer versiones sin fin si Meta responde «ya existe» a todo.</summary>
    public const int MaxCreateAttempts = 5;

    public async Task<IReadOnlyList<ManagedTemplateDto>> HandleAsync(ProvisionBillingTemplatesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        // El permiso y Mensajería son de núcleo; las plantillas de cobro son de Billing: sin su módulo no se crea nada en Meta.
        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, command.TenantId, TenantModuleKeys.ServiceContracts, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var now = clock.UtcNow;
        var memberId = await callerMembership.FindAsync(command.TenantId, cancellationToken);

        var mirror = (await repository.ListAsync(command.TenantId, command.ConnectionId, cancellationToken)).ToList();
        var rows = new List<MessageTemplate>();
        foreach (var definition in BillingTemplateDefinitions.All)
        {
            var current = mirror.FirstOrDefault(template => template.Kind == definition.Kind);
            if (current is { IsLive: true })
            {
                rows.Add(current);
                continue;
            }

            var body = ((definition.Kind == MessageTemplateKind.BillingReminder ? command.ReminderBody : command.OverdueBody) ?? definition.BaseBody).Trim();
            var nextVersion = (mirror
                .Where(template => string.Equals(template.Language, BillingTemplateDefinitions.Language, StringComparison.Ordinal))
                .Select(template => definition.VersionOf(template.Name))
                .Max() ?? 0) + 1;

            MessageTemplate template;
            try
            {
                var (made, name) = await CreateAsync(command, source, definition, body, nextVersion, cancellationToken);

                if (current is not null)
                {
                    await DeletePreviousAsync(command, source, current, cancellationToken);
                    repository.Remove(current);
                    mirror.Remove(current);
                }

                template = MessageTemplate.Pending(
                    command.TenantId, command.ConnectionId, made.Id, name, BillingTemplateDefinitions.Language, made.Category,
                    TemplateComponentsBuilder.Named, made.Status, TemplateComponentsBuilder.Components(definition.Draft(name, body)).ToJsonString(),
                    definition.Kind, memberId, now);
            }
            catch (MessagingDomainException error) when (error.Code == MessagingErrorCodes.TemplateRejectedByMeta)
            {
                // Dice cuál de los dos avisos fue: la pantalla muestra el mensaje tal cual y el admin tiene que saber qué texto corregir.
                throw new MessagingDomainException(error.Code, $"{definition.Label}: {error.Message}");
            }

            repository.Add(template);
            mirror.Add(template);
            rows.Add(template);
            audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateBillingProvisioned, template.Id, now);
            // Una plantilla a la vez: si Meta rechaza la siguiente, ésta ya quedó en el espejo y el reintento crea sólo la que falta.
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var names = await TemplateManagementSupport.MemberNamesAsync(
            memberNames, command.TenantId, rows.Where(row => row.CreatedByMemberId.HasValue).Select(row => row.CreatedByMemberId!.Value).Distinct().ToArray(), cancellationToken);
        return rows.Select(row => ManagedTemplateMapping.ToDto(row, names)).ToArray();
    }

    private async Task<(CreatedTemplate Created, string Name)> CreateAsync(
        ProvisionBillingTemplatesCommand command, MessagingTemplateSource source, BillingTemplateDefinition definition, string body, int firstVersion,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var name = definition.NameFor(firstVersion + attempt - 1);
            var result = await meta.CreateTemplateAsync(source, definition.Payload(name, body), cancellationToken);
            if (result.Succeeded && result.Value is { } value)
            {
                return (value, name);
            }

            var error = await TemplateGraphErrors.FailAsync(
                connections, command.TenantId, command.ConnectionId, result.Failure ?? new MessagingGraphFailure(0, null, "unreachable"), cancellationToken);
            if (error.Code != MessagingErrorCodes.TemplateNameTaken || attempt >= MaxCreateAttempts)
            {
                throw error;
            }
        }
    }

    private async Task DeletePreviousAsync(
        ProvisionBillingTemplatesCommand command, MessagingTemplateSource source, MessageTemplate previous, CancellationToken cancellationToken)
    {
        var deleted = await meta.DeleteTemplateAsync(source, previous.MetaTemplateId, previous.Name, cancellationToken);
        if (deleted.Failure is not { } failure)
        {
            return;
        }

        if (TemplateGraphErrors.IsTokenRejected(failure))
        {
            throw await TemplateGraphErrors.FailAsync(connections, command.TenantId, command.ConnectionId, failure, cancellationToken);
        }

        LogPreviousVersionNotDeleted(logger, command.TenantId, command.ConnectionId, previous.MetaTemplateId);
    }

    // Sin el token ni la respuesta de Meta: sólo lo que hace falta para encontrar la plantilla huérfana.
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Meta did not delete the previous billing template {MetaTemplateId} of connection {ConnectionId} in tenant {TenantId}; its mirror row is removed anyway and the next refresh brings it back as External if Meta still has it.")]
    private static partial void LogPreviousVersionNotDeleted(ILogger logger, Guid tenantId, Guid connectionId, string metaTemplateId);
}
