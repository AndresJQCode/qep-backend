using System.Globalization;
using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Cobros recurrentes §5 (plantillas aprobadas por el owner, 2026-10-29): una de las dos plantillas de cobro. <c>Examples</c> son los
/// ejemplos que Meta exige por variable, en orden: <c>{{1}}</c>, <c>{{2}}</c>… (posicionales; sin encabezado ni pie).
/// </summary>
public sealed record BillingTemplateDefinition(MessageTemplateKind Kind, string Name, string Body, IReadOnlyList<string> Examples)
{
    private string VersionPrefix => Name + "_v";

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

    /// <summary>Los valores del cuerpo con su clave posicional (<c>"1"</c>…), en orden.</summary>
    public IReadOnlyList<TemplateParameter> ParametersFor(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != Examples.Count)
        {
            throw new ArgumentException($"The template {Name} takes {Examples.Count} values.", nameof(values));
        }

        return values.Select((value, index) => new TemplateParameter((index + 1).ToString(CultureInfo.InvariantCulture), value)).ToArray();
    }

    /// <summary>Lo que verá la persona en la bandeja: el cuerpo con las variables sustituidas. Mismo reemplazo que <see cref="TemplateCatalog"/>.</summary>
    public string Render(IReadOnlyList<TemplateParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var values = parameters.ToDictionary(parameter => parameter.Key, parameter => parameter.Value, StringComparer.Ordinal);
        return TemplateCatalog.Placeholder().Replace(Body, match => values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
    }

    /// <summary>Los <c>components</c> de Meta: sólo el cuerpo, con <c>body_text</c> posicional (una fila con un ejemplo por variable).</summary>
    public JsonArray Components()
    {
        var example = new JsonArray();
        foreach (var value in Examples)
        {
            example.Add(value);
        }

        return new JsonArray(new JsonObject
        {
            ["type"] = "BODY",
            ["text"] = Body,
            ["example"] = new JsonObject { ["body_text"] = new JsonArray(example) },
        });
    }

    /// <summary>El payload de creación de la versión <paramref name="name"/>. Sin <c>parameter_format</c>: el valor por defecto de Meta es
    /// posicional.</summary>
    public JsonObject Payload(string name) => new()
    {
        ["name"] = name,
        ["language"] = BillingTemplateDefinitions.Language,
        ["category"] = BillingTemplateDefinitions.Category,
        ["components"] = Components(),
    };
}

/// <summary>Cobros recurrentes §5: la **única** fuente del nombre, idioma, categoría, texto y ejemplos de las dos plantillas de cobro. Texto
/// literal del owner.</summary>
public static class BillingTemplateDefinitions
{
    public const string Language = "es";
    public const string Category = "UTILITY";

    /// <summary>Lo que guarda la fila del espejo en <c>parameter_format</c>, como lo escribe Meta.</summary>
    public const string ParameterFormat = "POSITIONAL";

    public static readonly BillingTemplateDefinition Reminder = new(
        MessageTemplateKind.BillingReminder,
        "qep_cobro_recordatorio",
        "Hola {{1}}, te recordamos que tu servicio {{2}} por {{3}} vence el {{4}}. Si ya realizaste el pago, ignora este mensaje.",
        ["Ana Pérez", "Hosting plan pro", "$ 150.000", "10/03/2026"]);

    public static readonly BillingTemplateDefinition Overdue = new(
        MessageTemplateKind.BillingOverdue,
        "qep_cobro_vencido",
        "Hola {{1}}, tienes {{2}} cobro(s) vencido(s) por un total de {{3}}. Escríbenos para ponerte al día. Si ya pagaste, ignora este mensaje.",
        ["Ana Pérez", "2", "$ 450.000"]);

    public static readonly IReadOnlyList<BillingTemplateDefinition> All = [Reminder, Overdue];

    public static BillingTemplateDefinition For(MessageTemplateKind kind) =>
        All.FirstOrDefault(definition => definition.Kind == kind)
        ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a billing template kind.");
}

/// <summary>Cobros recurrentes §5, <c>POST …/template-management/billing</c>: aprovisiona (o reintenta) las dos plantillas de cobro de la conexión.</summary>
public sealed record ProvisionBillingTemplatesCommand(Guid TenantId, Guid ConnectionId) : ICommand<IReadOnlyList<ManagedTemplateDto>>;

/// <summary>
/// Cobros recurrentes §5: el aprovisionamiento de <see cref="QuotationTemplateDefinition"/> sin encabezado de documento ni adopción, para las
/// dos plantillas de cobro. Por cada definición, en orden:
/// <list type="number">
/// <item>Si la conexión ya tiene su fila de ese tipo viva (pendiente, aprobada o en apelación), no se toca: es idempotente.</item>
/// <item>El nombre a crear es la versión siguiente a la mayor del espejo para ese nombre base (cualquier kind, idioma <c>es</c>); sin ninguna, la 1.
/// Si Meta dice que el nombre ya existe (uno en cuarentena o creado a mano que el espejo no conoce), prueba la siguiente hasta
/// <see cref="MaxCreateAttempts"/> creaciones.</item>
/// <item>Con la nueva creada, borra en Meta la fila vieja del tipo (rechazada o pausada) y la quita del espejo: crear antes de borrar deja la
/// vieja intacta si la creación falla.</item>
/// </list>
/// Las filas nuevas y las quitadas se guardan juntas (una sola transacción) al final, con el índice único parcial
/// <c>UX_message_templates_billing</c> cerrando la carrera. Si la segunda definición falla después de crear la primera en Meta, la primera
/// queda en Meta y vuelve como <c>External</c> en el siguiente refresco; reintentar crea la versión siguiente.
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
    ILogger<ProvisionBillingTemplatesHandler> logger)
    : ICommandHandler<ProvisionBillingTemplatesCommand, IReadOnlyList<ManagedTemplateDto>>
{
    /// <summary>Creaciones por plantilla, contando la primera: un tope para no recorrer versiones sin fin si Meta responde «ya existe» a todo.</summary>
    public const int MaxCreateAttempts = 5;

    public async Task<IReadOnlyList<ManagedTemplateDto>> HandleAsync(ProvisionBillingTemplatesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        // El permiso y Mensajería son de núcleo; las plantillas de cobro son de Billing: sin su módulo no se crea nada en Meta.
        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, command.TenantId, TenantModuleKeys.Billing, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var now = clock.UtcNow;
        var memberId = await callerMembership.FindAsync(command.TenantId, cancellationToken);

        var mirror = (await repository.ListAsync(command.TenantId, command.ConnectionId, cancellationToken)).ToList();
        var rows = new List<MessageTemplate>();
        var created = false;
        foreach (var definition in BillingTemplateDefinitions.All)
        {
            var current = mirror.FirstOrDefault(template => template.Kind == definition.Kind);
            if (current is { IsLive: true })
            {
                rows.Add(current);
                continue;
            }

            var nextVersion = (mirror
                .Where(template => string.Equals(template.Language, BillingTemplateDefinitions.Language, StringComparison.Ordinal))
                .Select(template => definition.VersionOf(template.Name))
                .Max() ?? 0) + 1;
            var (made, name) = await CreateAsync(command, source, definition, nextVersion, cancellationToken);

            if (current is not null)
            {
                await DeletePreviousAsync(command, source, current, cancellationToken);
                repository.Remove(current);
                mirror.Remove(current);
            }

            var template = MessageTemplate.Pending(
                command.TenantId, command.ConnectionId, made.Id, name, BillingTemplateDefinitions.Language, made.Category,
                BillingTemplateDefinitions.ParameterFormat, made.Status, definition.Components().ToJsonString(), definition.Kind, memberId, now);
            repository.Add(template);
            mirror.Add(template);
            rows.Add(template);
            audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateBillingProvisioned, template.Id, now);
            created = true;
        }

        if (created)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var names = await TemplateManagementSupport.MemberNamesAsync(
            memberNames, command.TenantId, rows.Where(row => row.CreatedByMemberId.HasValue).Select(row => row.CreatedByMemberId!.Value).Distinct().ToArray(), cancellationToken);
        return rows.Select(row => ManagedTemplateMapping.ToDto(row, names)).ToArray();
    }

    private async Task<(CreatedTemplate Created, string Name)> CreateAsync(
        ProvisionBillingTemplatesCommand command, MessagingTemplateSource source, BillingTemplateDefinition definition, int firstVersion, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var name = definition.NameFor(firstVersion + attempt - 1);
            var result = await meta.CreateTemplateAsync(source, definition.Payload(name), cancellationToken);
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
