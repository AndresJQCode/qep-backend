using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-11-09 D1 y D2: la **única** fuente del nombre, idioma, categoría, texto base y ejemplos de la plantilla de pedido, calcada de
/// <see cref="QuotationTemplateDefinition"/>. El admin puede editar el texto al crearla, con las variables de
/// <see cref="SystemTemplateVariables.Order"/> (sin <c>vigencia</c>: un pedido no vence), y <see cref="BaseBody"/> es el que se usa si no
/// manda otro. El nombre va por versiones (<c>qep_pedido</c>, <c>qep_pedido_v2</c>…) por la misma razón que la cotización: Meta bloquea un
/// tiempo el nombre de una plantilla borrada. El sender lee el nombre vigente de la fila <c>Order</c>, no de una constante.
/// </summary>
public static class OrderTemplateDefinition
{
    public const string Name = MessageTemplateName.OrderName;
    public const string Language = "es";
    public const string Category = "UTILITY";
    public const string BaseBody = "Hola {{nombre_contacto}}, te enviamos el pedido {{numero_pedido}} por un total de {{valor_total}}. Adjuntamos el documento. ¡Gracias por tu compra!";
    public const string ExampleFileName = "pedido-ejemplo.pdf";
    public const string ExampleMimeType = "application/pdf";

    // «qep_pedido_v»: dentro del prefijo reservado, así que ninguna libre puede tomar una versión.
    private const string VersionPrefix = MessageTemplateName.OrderPrefix + "v";

    public static readonly IReadOnlyDictionary<string, string> Examples =
        SystemTemplateVariables.Order.ToDictionary(variable => variable.Key, variable => variable.Example, StringComparer.Ordinal);

    /// <summary>El nombre de la versión <paramref name="version"/>: la 1 es <see cref="Name"/>; de la 2 en adelante, <c>qep_pedido_v{n}</c>.</summary>
    public static string NameFor(int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return version == 1 ? Name : VersionPrefix + version.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>El inverso de <see cref="NameFor"/>, con las mismas reglas que la cotización: cualquier otro nombre es <c>null</c>.</summary>
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

    /// <summary>Sin pie, como la cotización.</summary>
    public static TemplateDraft Draft(string name, string body) => new(name, Language, Category, null, body, null, [], Examples);

    public static JsonObject Payload(string handle, string name, string body) => TemplateComponentsBuilder.CreatePayload(Draft(name, body), handle);

    /// <summary>D3: la misma forma que exige la cotización (encabezado de documento, sin botones, cuerpo no vacío) pero con las variables del
    /// pedido más las claves viejas; una con <c>vigencia</c> no es un pedido.</summary>
    public static bool IsAdoptable(string componentsJson)
    {
        ArgumentNullException.ThrowIfNull(componentsJson);
        var described = TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson);
        var allowed = SystemTemplateVariables.KeysOf(SystemTemplateVariables.Order);
        return !string.IsNullOrWhiteSpace(described.Body)
            && described.Header is null
            && described.UnsupportedReason == TemplateUnsupportedReasons.MediaHeader
            && described.Parameters.All(key => allowed.Contains(key) || SystemTemplateVariables.LegacyKeys.ContainsKey(key))
            && described.Buttons.Count == 0
            && IsDocumentHeader(componentsJson);
    }

    internal static string? StoredBody(string componentsJson) =>
        TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson).Body?.Trim();

    public static byte[] ExamplePdf() => MinimalPdf.Build("Pedido de ejemplo - QEP");

    private static bool IsDocumentHeader(string componentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(componentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Array
                && document.RootElement.EnumerateArray().Any(component =>
                    component.ValueKind == JsonValueKind.Object
                    && string.Equals(TemplateCatalog.Text(component, "type"), "HEADER", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(TemplateCatalog.Text(component, "format"), "DOCUMENT", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Spec 2026-11-09 D3, <c>POST …/template-management/order</c>: aprovisiona (o adopta, o reintenta) la plantilla de pedido.</summary>
/// <param name="Body">El cuerpo editado, o <c>null</c> para el texto base.</param>
public sealed record ProvisionOrderTemplateCommand(Guid TenantId, Guid ConnectionId, string? Body = null) : ICommand<ManagedTemplateDto>;

/// <summary>El cuerpo, si viene, usa sólo las variables del pedido (D2).</summary>
public sealed class ProvisionOrderTemplateCommandValidator : AbstractValidator<ProvisionOrderTemplateCommand>
{
    public ProvisionOrderTemplateCommandValidator() =>
        RuleFor(command => command.Body).Custom((body, context) =>
        {
            if (body is not null && TemplateBodyRules.Check(body, SystemTemplateVariables.KeysOf(SystemTemplateVariables.Order)) is { } error)
            {
                context.AddFailure(new ValidationFailure("body", error));
            }
        });
}

/// <summary>
/// Spec 2026-11-09 D3: el mismo procedimiento que <see cref="ProvisionQuotationTemplateHandler"/>, paso por paso, con la fila <c>Order</c>:
/// viva ⇒ <c>order_exists</c>; externa <c>qep_pedido</c>/<c>es</c> viva y compatible ⇒ adopción (auditada con <c>["kind"]</c>), incompatible ⇒
/// <c>order_conflict</c>; versión siguiente a la mayor <c>qep_pedido*</c> del espejo; una subida y hasta <see cref="MaxCreateAttempts"/>
/// creaciones ante <c>name_taken</c>; crear antes de borrar la rechazada; una sola transacción. La carrera entre dos primeros
/// aprovisionamientos la cierra <c>UX_message_templates_order</c>. Las razones de cada regla están en el handler de la cotización y no se
/// repiten acá.
/// </summary>
public sealed partial class ProvisionOrderTemplateHandler(
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
    ILogger<ProvisionOrderTemplateHandler> logger,
    IValidator<ProvisionOrderTemplateCommand> validator)
    : ICommandHandler<ProvisionOrderTemplateCommand, ManagedTemplateDto>
{
    public const int MaxCreateAttempts = ProvisionQuotationTemplateHandler.MaxCreateAttempts;

    private static readonly string[] AdoptedFields = ["kind"];

    public async Task<ManagedTemplateDto> HandleAsync(ProvisionOrderTemplateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var now = clock.UtcNow;
        var body = (command.Body ?? OrderTemplateDefinition.BaseBody).Trim();

        var current = await repository.FindOrderAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (current is { IsLive: true })
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateOrderExists, "This connection already has its order template pending or approved.");
        }

        if (current is null
            && await repository.FindByNameAsync(command.TenantId, command.ConnectionId, OrderTemplateDefinition.Name, OrderTemplateDefinition.Language, cancellationToken) is { IsLive: true } external)
        {
            return await AdoptAsync(command, external, body, now, cancellationToken);
        }

        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var mirror = await repository.ListAsync(command.TenantId, command.ConnectionId, cancellationToken);
        var nextVersion = (mirror
            .Where(template => string.Equals(template.Language, OrderTemplateDefinition.Language, StringComparison.Ordinal))
            .Select(template => OrderTemplateDefinition.VersionOf(template.Name))
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
            command.TenantId, command.ConnectionId, created.Id, name, OrderTemplateDefinition.Language, created.Category, TemplateComponentsBuilder.Named,
            created.Status, TemplateComponentsBuilder.Components(OrderTemplateDefinition.Draft(name, body), handle).ToJsonString(), MessageTemplateKind.Order,
            memberId, now);
        repository.Add(template);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateOrderProvisioned, template.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(template, await NamesOfAsync(command.TenantId, template, cancellationToken));
    }

    private async Task<ManagedTemplateDto> AdoptAsync(
        ProvisionOrderTemplateCommand command, MessageTemplate external, string body, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!OrderTemplateDefinition.IsAdoptable(external.ComponentsJson)
            || (command.Body is not null && !string.Equals(OrderTemplateDefinition.StoredBody(external.ComponentsJson), body, StringComparison.Ordinal)))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateOrderConflict,
                $"A template named '{OrderTemplateDefinition.Name}' already exists in Meta with another body or an incompatible shape; delete it from the templates screen first.");
        }

        external.AdoptAsOrder(now);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateOrderProvisioned, external.Id, now, AdoptedFields);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ManagedTemplateMapping.ToDto(external, await NamesOfAsync(command.TenantId, external, cancellationToken));
    }

    private async Task<string> UploadExampleAsync(ProvisionOrderTemplateCommand command, MessagingTemplateSource source, CancellationToken cancellationToken)
    {
        var upload = await meta.UploadTemplateExampleAsync(
            source, OrderTemplateDefinition.ExamplePdf(), OrderTemplateDefinition.ExampleMimeType, OrderTemplateDefinition.ExampleFileName, cancellationToken);
        if (upload.Succeeded && upload.Value is { } handle)
        {
            return handle;
        }

        // Como en la cotización (revisión final F1): la subida es un endpoint de la app, no de la WABA; nunca se reporta la conexión.
        var failure = upload.Failure ?? new MessagingGraphFailure(0, null, "no_handle");
        LogUploadFailed(
            logger, command.TenantId, command.ConnectionId, failure.HttpStatus, failure.Code?.ToString(CultureInfo.InvariantCulture) ?? "none", failure.Reason);
        throw new MessagingDomainException(MessagingErrorCodes.TemplateUploadFailed, "WhatsApp did not accept the example PDF of the order template.");
    }

    private async Task<(CreatedTemplate Created, string Name)> CreateAsync(
        ProvisionOrderTemplateCommand command, MessagingTemplateSource source, string handle, string body, int firstVersion, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var name = OrderTemplateDefinition.NameFor(firstVersion + attempt - 1);
            var created = await meta.CreateTemplateAsync(source, OrderTemplateDefinition.Payload(handle, name, body), cancellationToken);
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
        Message = "Meta did not delete the previous order template {MetaTemplateId} of connection {ConnectionId} in tenant {TenantId}; its mirror row is removed anyway and the next refresh brings it back as External if Meta still has it.")]
    private static partial void LogPreviousVersionNotDeleted(ILogger logger, Guid tenantId, Guid connectionId, string metaTemplateId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Meta did not accept the example PDF of the order template for connection {ConnectionId} in tenant {TenantId}: status {HttpStatus}, code {GraphCode}, reason {Reason}; the connection is not reported.")]
    private static partial void LogUploadFailed(ILogger logger, Guid tenantId, Guid connectionId, int httpStatus, string graphCode, string reason);
}
