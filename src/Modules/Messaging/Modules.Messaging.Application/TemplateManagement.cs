using System.Globalization;
using System.Text.RegularExpressions;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

// Spec 2026-10-14 §4.3: la administración de plantillas. Todo handler revalida tenant, permiso y módulo antes de tocar nada (403 con
// código, nunca 404 ante otro tenant) y resuelve la conexión con ResolveTemplateSourceAsync (null = connection_unavailable, 422).

internal static class TemplateManagementSupport
{
    public static MessagingDomainException ConnectionUnavailable() =>
        new(MessagingErrorCodes.ConnectionUnavailable, "The WhatsApp connection is paused, needs attention or was deleted.");

    public static ResourceNotFoundException NotFound(Guid templateId) =>
        new(MessagingErrorCodes.TemplateNotFound, $"Template '{templateId}' was not found in this connection.");

    /// <summary>Los nombres para mostrar de los miembros que aparecen como autores; vacío si no hay ninguno, sin ir a buscar. Lo comparten
    /// la pantalla, la creación libre y el aprovisionamiento de la cotización.</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> MemberNamesAsync(
        IMessagingMemberNames memberNames, Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
        memberIds.Count == 0 ? new Dictionary<Guid, string>() : await memberNames.FindAsync(tenantId, memberIds, cancellationToken);
}

/// <summary>§4.3: una fila del espejo al DTO de la pantalla, con el texto resuelto por <see cref="TemplateCatalog.FromStored"/>.</summary>
internal static class ManagedTemplateMapping
{
    public static ManagedTemplateDto ToDto(MessageTemplate template, IReadOnlyDictionary<Guid, string> memberNames)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(memberNames);
        // Con la imagen guardada, la pantalla dice «enviable» igual que el catálogo del chat (spec 2026-11-17).
        var described = TemplateCatalog.FromStored(
            template.Name, template.Language, template.Category, template.ParameterFormat, template.ComponentsJson, template.HeaderImageUrl);
        return new ManagedTemplateDto(
            template.Id,
            template.MetaTemplateId,
            template.Name,
            template.Language,
            template.Category,
            described.ParameterFormat,
            template.Status,
            template.StatusReason,
            template.Kind.ToString(),
            described.Header,
            described.Body,
            described.Footer,
            described.Buttons,
            described.Parameters,
            described.Sendable,
            described.UnsupportedReason,
            template.CreatedByMemberId is { } member ? new MemberRefDto(member, memberNames.GetValueOrDefault(member) ?? MessageMapping.DeletedMemberName) : null,
            template.CreatedAt,
            template.UpdatedAt,
            BillingTemplateDefinitions.RequestedCategoryFor(template.Kind, template.Name, template.Category),
            template.HeaderImageUrl,
            described.HeaderMedia);
    }
}

/// <summary>§4.3: arma <see cref="TemplateManagementDto"/> con una llamada a cada directorio. Público sólo para que Infrastructure lo
/// registre; no es API para otros módulos.</summary>
public sealed class TemplateManagementReader(IMessageTemplateRepository repository, IMessagingConnectionDirectory connections, IMessagingMemberNames memberNames)
{
    public async Task<TemplateManagementDto> ReadAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = (await connections.ListActiveAsync(tenantId, cancellationToken)).FirstOrDefault(active => active.Id == connectionId)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var templates = await repository.ListAsync(tenantId, connectionId, cancellationToken);
        var creators = templates.Where(template => template.CreatedByMemberId is not null).Select(template => template.CreatedByMemberId!.Value).Distinct().ToArray();
        var names = await TemplateManagementSupport.MemberNamesAsync(memberNames, tenantId, creators, cancellationToken);
        var quotation = templates.FirstOrDefault(template => template.Kind == MessageTemplateKind.Quotation);
        var order = templates.FirstOrDefault(template => template.Kind == MessageTemplateKind.Order);
        var supportReply = templates.FirstOrDefault(template => template.Kind == MessageTemplateKind.SupportReply);
        var supportFormalReply = templates.FirstOrDefault(template => template.Kind == MessageTemplateKind.SupportFormalReply);
        var supportSurvey = templates.FirstOrDefault(template => template.Kind == MessageTemplateKind.SupportSurvey);
        var wholesaleNotice = templates.FirstOrDefault(template => template.Kind == MessageTemplateKind.WholesaleInvitationNotice);
        var invoice = templates.FirstOrDefault(template => template.Kind == MessageTemplateKind.Invoice);
        var taskNotice = templates.FirstOrDefault(template => template.Kind == MessageTemplateKind.TaskNotice);
        var appointmentReminder = templates.FirstOrDefault(template => template.Kind == MessageTemplateKind.AppointmentReminder);
        return new TemplateManagementDto(
            connection,
            quotation is null ? null : new QuotationTemplateStatusDto(quotation.Id, quotation.Status, quotation.StatusReason),
            order is null ? null : new OrderTemplateStatusDto(order.Id, order.Status, order.StatusReason),
            await repository.LastSyncAsync(tenantId, connectionId, cancellationToken),
            templates.Select(template => ManagedTemplateMapping.ToDto(template, names)).ToArray(),
            supportReply is null ? null : new SupportReplyTemplateStatusDto(supportReply.Id, supportReply.Status, supportReply.StatusReason),
            supportFormalReply is null
                ? null
                : new SupportFormalReplyTemplateStatusDto(supportFormalReply.Id, supportFormalReply.Status, supportFormalReply.StatusReason),
            supportSurvey is null ? null : new SupportSurveyTemplateStatusDto(supportSurvey.Id, supportSurvey.Status, supportSurvey.StatusReason),
            wholesaleNotice is null
                ? null
                : new WholesaleInvitationNoticeTemplateStatusDto(wholesaleNotice.Id, wholesaleNotice.Status, wholesaleNotice.StatusReason),
            invoice is null ? null : new InvoiceTemplateStatusDto(invoice.Id, invoice.Status, invoice.StatusReason),
            taskNotice is null ? null : new TaskNoticeTemplateStatusDto(taskNotice.Id, taskNotice.Status, taskNotice.StatusReason),
            appointmentReminder is null
                ? null
                : new AppointmentReminderTemplateStatusDto(appointmentReminder.Id, appointmentReminder.Status, appointmentReminder.StatusReason));
    }
}

/// <summary>§4.7 <c>GET /connections/{connectionId}/template-management</c>. Si la conexión nunca se sincronizó, la primera lectura llena el
/// espejo (igual que el catálogo de envío, y como él ignora la carrera de dos primeros llenados): la pantalla no puede mostrar «cero plantillas» de un WABA que tiene veinte.</summary>
public sealed record GetTemplateManagementQuery(Guid TenantId, Guid ConnectionId) : IQuery<TemplateManagementDto>;

public sealed class GetTemplateManagementHandler(
    TemplateManagementReader reader,
    IMessageTemplateRepository repository,
    TemplateMirrorSync sync,
    IMessagingConnectionDirectory connections,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetTemplateManagementQuery, TemplateManagementDto>
{
    public async Task<TemplateManagementDto> HandleAsync(GetTemplateManagementQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(query.TenantId, query.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        if (await repository.LastSyncAsync(query.TenantId, query.ConnectionId, cancellationToken) is null)
        {
            try
            {
                await sync.RunAsync(query.TenantId, query.ConnectionId, source, cancellationToken);
            }
            catch (RequestConcurrencyException)
            {
                // La otra primera sincronización ganó y ya llenó el espejo (igual que MirroredTemplateCatalog): se sigue leyendo de ahí.
                // El POST …/sync explícito sí deja subir el 412.
            }
        }

        return await reader.ReadAsync(query.TenantId, query.ConnectionId, cancellationToken);
    }
}

/// <summary>§4.7 <c>POST …/template-management/sync</c>: sincroniza contra Meta y devuelve la pantalla.</summary>
public sealed record SyncTemplatesCommand(Guid TenantId, Guid ConnectionId) : ICommand<TemplateManagementDto>;

public sealed class SyncTemplatesHandler(
    TemplateManagementReader reader,
    TemplateMirrorSync sync,
    IMessagingConnectionDirectory connections,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : ICommandHandler<SyncTemplatesCommand, TemplateManagementDto>
{
    public async Task<TemplateManagementDto> HandleAsync(SyncTemplatesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        await sync.RunAsync(command.TenantId, command.ConnectionId, source, cancellationToken);
        return await reader.ReadAsync(command.TenantId, command.ConnectionId, cancellationToken);
    }
}

/// <summary>Los textos de <c>errors</c>, tuteando.</summary>
public static class TemplateMessages
{
    public const string Name = "El nombre lleva letras minúsculas, números y guiones bajos, hasta 512 caracteres.";
    public const string NameReserved = "El nombre no puede ser «qep_cotizacion» ni empezar por «qep_cotizacion_»: está reservado para la plantilla de cotización.";
    public const string Language = "Elige un idioma con la forma «es» o «es_CO».";
    public const string Category = "La categoría es MARKETING o UTILITY.";
    public const string Header = "El encabezado lleva hasta 60 caracteres y no admite variables.";
    public const string HeaderExclusive = "Elige un encabezado de texto o una imagen, no los dos.";
    public const string HeaderImage = "Sube una imagen JPG o PNG de hasta 5 MB.";
    public const string Footer = "El pie lleva hasta 60 caracteres.";
    public const string Body = "Escribe un cuerpo de 1 a 1024 caracteres con algo más que variables.";
    public const string BodyVariable = "Las variables van entre llaves dobles, en minúsculas, con números o guiones bajos y sin espacios: {{nombre}}.";
    public const string BodyVariableNotAllowed = "Usa sólo las variables de la lista de esta plantilla.";
    public const string Examples = "Escribe un ejemplo para cada variable del cuerpo, ni una más ni una menos.";
    public const string Buttons = "Hasta diez botones; los de respuesta rápida van todos juntos, al principio o al final.";
    public const string ButtonType = "El tipo de botón es QuickReply, Url o PhoneNumber.";
    public const string ButtonText = "El texto del botón lleva de 1 a 25 caracteres.";
    public const string ButtonUrl = "El botón de enlace necesita una URL https completa, sin variables, de hasta 2000 caracteres.";
    public const string ButtonPhone = "El botón de llamada necesita un número de 7 a 20 dígitos, con «+» opcional.";
}

/// <summary>§4.7 <c>POST …/template-management/templates</c>. Todo opcional para que lo que falte salga 422 con su clave en <c>errors</c>.
/// <c>HeaderImageFileId</c> (spec 2026-11-17) es la imagen fija del encabezado, ya subida por la sesión de la imagen.</summary>
public sealed record CreateTemplateCommand(
    Guid TenantId,
    Guid ConnectionId,
    string? Name,
    string? Language,
    string? Category,
    string? Header,
    string? Body,
    string? Footer,
    IReadOnlyList<TemplateButtonInput>? Buttons,
    IReadOnlyDictionary<string, string?>? Examples,
    Guid? HeaderImageFileId = null) : ICommand<ManagedTemplateDto>;

/// <summary>§4.8. Lo que Meta valida y nosotros no (proporción de variables frente a texto, variable al final) vuelve de Meta como
/// <c>rejected_by_meta</c>: duplicarlo sería mantener dos copias de reglas que Meta cambia sin avisar.</summary>
public sealed partial class CreateTemplateCommandValidator : AbstractValidator<CreateTemplateCommand>
{
    private static readonly string[] Categories = ["MARKETING", "UTILITY"];
    private static readonly string[] ButtonTypes = ["QuickReply", "Url", "PhoneNumber"];

    public CreateTemplateCommandValidator()
    {
        // Dos mensajes distintos: «reservado» no es lo mismo que «mal formado», y el primero explica por qué un nombre bien formado se rechaza.
        RuleFor(command => command.Name)
            .Must(name => MessageTemplateName.IsValid(name))
            .OverridePropertyName("name").WithMessage(TemplateMessages.Name)
            .Must(name => !MessageTemplateName.IsReserved(name))
            .OverridePropertyName("name").WithMessage(TemplateMessages.NameReserved);
        RuleFor(command => command.Language)
            .Must(language => language is not null && LanguagePattern().IsMatch(language))
            .OverridePropertyName("language").WithMessage(TemplateMessages.Language);
        RuleFor(command => command.Category)
            .Must(category => category is not null && Categories.Contains(category, StringComparer.Ordinal))
            .OverridePropertyName("category").WithMessage(TemplateMessages.Category);
        RuleFor(command => command.Header)
            .Must(header => header is null || (header.Length <= 60 && !header.Contains("{{", StringComparison.Ordinal)))
            .OverridePropertyName("header").WithMessage(TemplateMessages.Header);
        // Spec 2026-11-17: el encabezado es de texto o es una imagen. Va en «header» porque es el campo que el usuario escribió de más.
        RuleFor(command => command.Header)
            .Must((command, header) => command.HeaderImageFileId is null || string.IsNullOrEmpty(header))
            .OverridePropertyName("header").WithMessage(TemplateMessages.HeaderExclusive);
        RuleFor(command => command.Footer)
            .Must(footer => footer is null || footer.Length <= 60)
            .OverridePropertyName("footer").WithMessage(TemplateMessages.Footer);
        // Custom con ValidationFailure a mano: los mensajes llevan {{nombre}} literal y WithMessage lo trataría como placeholder.
        RuleFor(command => command.Body).Custom((body, context) =>
        {
            if (TemplateBodyRules.Check(body) is { } error)
            {
                context.AddFailure(new ValidationFailure("body", error));
            }
        });
        RuleFor(command => command.Examples).Custom((given, context) =>
        {
            var command = context.InstanceToValidate;
            if (string.IsNullOrWhiteSpace(command.Body) || command.Body.Length > 1024)
            {
                return; // ya marcado en body; sin cuerpo no hay contra qué comparar los ejemplos.
            }

            var variables = TemplateComponentsBuilder.VariablesOf(command.Body);
            var examples = given ?? new Dictionary<string, string?>();
            var keysMatch = examples.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(variables);
            if (!keysMatch || examples.Values.Any(string.IsNullOrWhiteSpace) || examples.Values.Any(value => value!.Length > 1024))
            {
                context.AddFailure(new ValidationFailure("examples", TemplateMessages.Examples));
            }
        });
        RuleFor(command => command.Buttons).Custom((buttons, context) =>
        {
            var list = buttons ?? [];
            if (list.Count > 10)
            {
                context.AddFailure(new ValidationFailure("buttons", TemplateMessages.Buttons));
                return;
            }

            for (var index = 0; index < list.Count; index++)
            {
                var button = list[index];
                if (button.Type is null || !ButtonTypes.Contains(button.Type, StringComparer.Ordinal))
                {
                    context.AddFailure(new ValidationFailure($"buttons[{index}].type", TemplateMessages.ButtonType));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(button.Text) || button.Text.Length > 25)
                {
                    context.AddFailure(new ValidationFailure($"buttons[{index}].text", TemplateMessages.ButtonText));
                }

                if (button.Type == "Url" && !IsHttpsUrl(button.Url))
                {
                    context.AddFailure(new ValidationFailure($"buttons[{index}].url", TemplateMessages.ButtonUrl));
                }

                if (button.Type == "PhoneNumber" && (button.PhoneNumber is null || !PhonePattern().IsMatch(button.PhoneNumber)))
                {
                    context.AddFailure(new ValidationFailure($"buttons[{index}].phoneNumber", TemplateMessages.ButtonPhone));
                }
            }

            // Meta exige las respuestas rápidas agrupadas: todas al principio o todas al final.
            var quick = list.Select((button, index) => (button.Type, index)).Where(pair => pair.Type == "QuickReply").Select(pair => pair.index).ToArray();
            if (quick.Length > 0 && quick.Length < list.Count)
            {
                var contiguous = quick[^1] - quick[0] + 1 == quick.Length;
                var atEdge = quick[0] == 0 || quick[^1] == list.Count - 1;
                if (!contiguous || !atEdge)
                {
                    context.AddFailure(new ValidationFailure("buttons", TemplateMessages.Buttons));
                }
            }
        });
    }

    private static bool IsHttpsUrl(string? url) =>
        url is { Length: <= 2000 } && !url.Contains("{{", StringComparison.Ordinal)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    [GeneratedRegex(@"^[a-z]{2,3}(_[A-Z]{2})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex LanguagePattern();

    [GeneratedRegex(@"^\+?[0-9]{7,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();
}

/// <summary>§4.3 «CreateTemplateCommand»: valida, comprueba el nombre en el espejo sin llamar a Meta, crea en Meta, inserta <c>PENDING</c>
/// <c>Free</c>, audita y guarda. Si Meta falla no se escribe nada.
/// <para>Spec 2026-11-17, con <c>HeaderImageFileId</c>: después de lo anterior (nada de eso toca el archivo) completa el archivo y lo revisa
/// contra <see cref="TemplateHeaderImagePolicy"/>, lo publica, sube sus bytes a Meta para el <c>header_handle</c> y crea con un encabezado
/// <c>IMAGE</c>. Desde que se publica, cualquier fallo (Meta rechaza, la subida a Meta falla, el nombre se pierde al guardar) libera la
/// imagen antes de dejar salir el error: ninguna imagen pública sobrevive sin su fila.</para></summary>
public sealed partial class CreateTemplateHandler(
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
    IValidator<CreateTemplateCommand> validator,
    IMessagingTemplateImageStore images,
    ITemplateHeaderImageReleases imageReleases,
    ILogger<CreateTemplateHandler> logger)
    : ICommandHandler<CreateTemplateCommand, ManagedTemplateDto>
{
    private readonly TemplateHeaderImageIntake _intake = new(images, repository, unitOfWork, imageReleases, clock, logger);

    public async Task<ManagedTemplateDto> HandleAsync(CreateTemplateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        // El validador garantiza Name, Language, Category y Body; Examples puede ser null (cuerpo sin variables) y se trata como vacío.
        var draft = new TemplateDraft(
            command.Name!, command.Language!, command.Category!, command.Header, command.Body!, command.Footer, command.Buttons ?? [],
            (command.Examples ?? new Dictionary<string, string?>()).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal));
        if (await repository.FindByNameAsync(command.TenantId, command.ConnectionId, draft.Name, draft.Language, cancellationToken) is not null)
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateNameTaken, "Another template of this connection already uses that name in that language.");
        }

        if (command.HeaderImageFileId is not { } fileId)
        {
            var template = await CreateAsync(command, source, draft, mediaHeader: null, attach: null, cancellationToken);
            return await ToDtoAsync(command, template, cancellationToken);
        }

        // Revisión del bloque C: un archivo que ya es la imagen de una plantilla guardada (doble envío, un fileId reenviado) no se toma.
        // Si se tomara, el fallo de esta creación liberaría la imagen de la otra, que sigue enviándose. No se toca el archivo.
        if (await repository.IsHeaderImageInUseAsync(command.TenantId, fileId, cancellationToken))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.TemplateHeaderImageRejected, "The header image already belongs to another template.",
                fieldErrors: TemplateHeaderImageIntake.HeaderImageField);
        }

        var mimeType = await _intake.EnsureUsableImageAsync(command.TenantId, fileId, cancellationToken);
        // Desde la publicación la imagen puede ser pública. Hasta que la fila quede guardada, cualquier salida la libera, también una
        // cancelación del request: una imagen pública sin fila no puede quedar. Con la fila guardada nunca se libera: es su imagen.
        // La publicación va dentro (revisión final): si falla después de copiar al bucket público (el guardado de Storage, una cancelación),
        // las copias públicas no quedan sin fila. Liberar un archivo a medio publicar o sin publicar es seguro: ReleaseAsync despublica lo
        // que haya y lo borra.
        var saved = false;
        try
        {
            var publicUrl = await images.PublishAsync(command.TenantId, fileId, cancellationToken);
            var bytes = await images.ReadAsync(command.TenantId, fileId, cancellationToken);
            var handle = await UploadImageAsync(command, source, bytes, mimeType, cancellationToken);
            var withImage = await CreateAsync(
                command, source, draft, TemplateMediaHeader.Image(handle), (template, now) => template.AttachHeaderImage(fileId, publicUrl, now), cancellationToken);
            saved = true; // CreateAsync termina con SaveChangesAsync.
            return await ToDtoAsync(command, withImage, cancellationToken);
        }
        catch (Exception) when (!saved)
        {
            await _intake.ReleaseOrDeferAsync(command.TenantId, fileId);
            throw;
        }
    }

    private async Task<MessageTemplate> CreateAsync(
        CreateTemplateCommand command, MessagingTemplateSource source, TemplateDraft draft, TemplateMediaHeader? mediaHeader,
        Action<MessageTemplate, DateTimeOffset>? attach, CancellationToken cancellationToken)
    {
        var created = await meta.CreateTemplateAsync(source, TemplateComponentsBuilder.CreatePayload(draft, mediaHeader), cancellationToken);
        if (!created.Succeeded || created.Value is null)
        {
            throw await TemplateGraphErrors.FailAsync(
                connections, command.TenantId, command.ConnectionId, created.Failure ?? new MessagingGraphFailure(0, null, "unreachable"), cancellationToken);
        }

        var now = clock.UtcNow;
        var memberId = await callerMembership.FindAsync(command.TenantId, cancellationToken);
        var template = MessageTemplate.Pending(
            command.TenantId, command.ConnectionId, created.Value.Id, draft.Name, draft.Language, created.Value.Category, TemplateComponentsBuilder.Named,
            created.Value.Status, TemplateComponentsBuilder.Components(draft, mediaHeader).ToJsonString(), MessageTemplateKind.Free, memberId, now);
        attach?.Invoke(template, now);
        repository.Add(template);
        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateCreated, template.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return template;
    }

    private async Task<ManagedTemplateDto> ToDtoAsync(CreateTemplateCommand command, MessageTemplate template, CancellationToken cancellationToken)
    {
        var names = await TemplateManagementSupport.MemberNamesAsync(
            memberNames, command.TenantId, template.CreatedByMemberId is { } member ? [member] : [], cancellationToken);
        return ManagedTemplateMapping.ToDto(template, names);
    }

    /// <summary>Como el PDF del pedido (<c>OrderTemplateProvisioning</c>): la subida es un endpoint de la app, no de la WABA, así que nunca
    /// se reporta la conexión; <c>no_app_id</c> se registra y sale como <c>upload_failed</c>.</summary>
    private async Task<string> UploadImageAsync(
        CreateTemplateCommand command, MessagingTemplateSource source, byte[] bytes, string mimeType, CancellationToken cancellationToken)
    {
        var fileName = mimeType == "image/png" ? "encabezado.png" : "encabezado.jpg";
        var upload = await meta.UploadTemplateExampleAsync(source, bytes, mimeType, fileName, cancellationToken);
        if (upload.Succeeded && upload.Value is { } handle)
        {
            return handle;
        }

        var failure = upload.Failure ?? new MessagingGraphFailure(0, null, "no_handle");
        LogUploadFailed(
            logger, command.TenantId, command.ConnectionId, failure.HttpStatus, failure.Code?.ToString(CultureInfo.InvariantCulture) ?? "none", failure.Reason);
        throw new MessagingDomainException(MessagingErrorCodes.TemplateUploadFailed, "WhatsApp did not accept the header image of the template.");
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Meta did not accept the header image of a template for connection {ConnectionId} in tenant {TenantId}: status {HttpStatus}, code {GraphCode}, reason {Reason}; the connection is not reported.")]
    private static partial void LogUploadFailed(ILogger logger, Guid tenantId, Guid connectionId, int httpStatus, string graphCode, string reason);
}

/// <summary>§4.7 <c>DELETE …/template-management/templates/{templateId}</c>. D-T11: en Meta sólo ese idioma; D-T12: no si una campaña
/// programada o enviándose la usa; D-T13: la fila se elimina, también cuando Meta dice que ya no existe. La de cotización sí se puede
/// borrar (§4.5): es la salida cuando algo quedó mal.</summary>
public sealed record DeleteTemplateCommand(Guid TenantId, Guid ConnectionId, Guid TemplateId) : ICommand<bool>;

public sealed class DeleteTemplateHandler(
    IMessageTemplateRepository repository,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    ITemplateHeaderImageReleases imageReleases)
    : ICommandHandler<DeleteTemplateCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteTemplateCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, MessagingPermissions.TemplateManage, cancellationToken);
        var source = await connections.ResolveTemplateSourceAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw TemplateManagementSupport.ConnectionUnavailable();
        var template = await repository.FindAsync(command.TenantId, command.ConnectionId, command.TemplateId, cancellationToken)
            ?? throw TemplateManagementSupport.NotFound(command.TemplateId);
        if (await repository.IsReferencedByActiveCampaignAsync(command.TenantId, command.ConnectionId, template.Name, template.Language, cancellationToken))
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplateInUseByCampaign, "A scheduled or sending campaign uses this template.");
        }

        var deleted = await meta.DeleteTemplateAsync(source, template.MetaTemplateId, template.Name, cancellationToken);
        if (!deleted.Succeeded)
        {
            throw await TemplateGraphErrors.FailAsync(
                connections, command.TenantId, command.ConnectionId, deleted.Failure ?? new MessagingGraphFailure(0, null, "unreachable"),
                TemplateGraphOperation.Delete, cancellationToken);
        }

        var now = clock.UtcNow;
        // Spec 2026-11-20: las imágenes retiradas se leen antes de quitar la fila (la cascada se las lleva al guardar).
        var retiredFileIds = await repository.ListRetiredHeaderImageFileIdsAsync(command.TenantId, template.Id, cancellationToken);
        repository.Remove(template);
        // Spec 2026-11-17: la imagen del encabezado la libera Storage al leer el aviso, que commitea con la eliminación de la fila. Si el
        // guardado falla, no sale aviso y la imagen sigue siendo de su fila.
        if (template.HeaderImageFileId is { } fileId)
        {
            imageReleases.Publish(command.TenantId, fileId, now);
        }

        foreach (var retiredFileId in retiredFileIds)
        {
            imageReleases.Publish(command.TenantId, retiredFileId, now);
        }

        audit.RecordTemplate(command.TenantId, executionContext.SubjectId, MessagingAuditActions.TemplateDeleted, template.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
