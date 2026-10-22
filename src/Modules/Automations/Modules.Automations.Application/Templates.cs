using System.Text.RegularExpressions;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Flow;
using Modules.Tenancy.Application;

namespace Modules.Automations.Application;

// Spec 2026-10-21 D-C8, D-C14 y §4.5: las plantillas del sistema (JSON embebido en Infrastructure) y las del tenant
// (automations.chatbot_templates): el catálogo, guardar un chatbot como plantilla, borrarla y sustituir sus parámetros al instalar.

/// <summary>Un hueco que la plantilla pide llenar antes de instalarse. <see cref="Kind"/> es <c>text</c> o <c>select</c> (con
/// <see cref="Options"/>), como lo pinta el frontend.</summary>
public sealed record TemplateParameter(string Key, string Label, string Kind, IReadOnlyList<string>? Options, string DefaultValue);

/// <summary>Una plantilla del sistema tal como la trae su archivo (§4.5). <see cref="FlowJson"/> está en la forma del modelo
/// (<see cref="Domain.Flow.FlowJson.Serialize"/>) y todavía con sus <c>{param.&lt;key&gt;}</c>; <see cref="LayoutJson"/>, tal cual.</summary>
public sealed record SystemTemplate(
    string Id,
    string Name,
    string Category,
    string Description,
    IReadOnlyList<string> Channels,
    string? Requirement,
    IReadOnlyList<TemplateParameter> Parameters,
    IReadOnlyList<string> Preview,
    string FlowJson,
    string LayoutJson);

/// <summary>D-C8: las plantillas del sistema, cargadas una vez al arrancar desde los recursos embebidos de Infrastructure.</summary>
public interface ISystemTemplateCatalog
{
    IReadOnlyList<SystemTemplate> All { get; }

    SystemTemplate? Find(string id);
}

public sealed record TemplateParameterDto(string Key, string Label, string Kind, IReadOnlyList<string>? Options, string DefaultValue);

/// <summary>§4.4. <see cref="Id"/> es el id estable de una del sistema o el Guid de una del tenant; <see cref="Preview"/>, los
/// títulos de los bloques en el orden del flujo.</summary>
public sealed record TemplateDto(
    string Id,
    string Name,
    string Category,
    string Description,
    int BlockCount,
    IReadOnlyList<string> Channels,
    string? Requirement,
    bool OwnedByTenant,
    IReadOnlyList<string> Preview,
    IReadOnlyList<TemplateParameterDto> Parameters);

public sealed record TemplateCatalogDto(IReadOnlyList<TemplateDto> System, IReadOnlyList<TemplateDto> Mine);

// «Chatbot» en el nombre: Messaging ya tiene ListTemplatesQuery y DeleteTemplateCommand (plantillas de WhatsApp) y el Bootstrapper
// importa los dos espacios de nombres.
public sealed record ListChatbotTemplatesQuery(Guid TenantId) : IQuery<TemplateCatalogDto>;

public sealed record SaveChatbotAsTemplateCommand(Guid TenantId, Guid ChatbotId, string Name, string Category, string Description)
    : ICommand<TemplateDto>;

/// <summary><see cref="TemplateId"/> llega como texto: un id del sistema es válido en la ruta y tiene que dar 404, no 400.</summary>
public sealed record DeleteChatbotTemplateCommand(Guid TenantId, string TemplateId) : ICommand<bool>;

/// <summary>
/// D-C14: <c>{param.&lt;key&gt;}</c> se sustituye una sola vez, al instalar, y sólo en los campos de texto del flujo: títulos, textos,
/// etiquetas y títulos de botones y filas, la descripción de una fila, el botón URL (etiqueta y URL), el texto de Pasar a un asesor y
/// de Resolver y las palabras clave de una pregunta. Nunca en ids, <c>saveTo</c>, nombres de variable, rutas, query, cuerpo ni mapeo:
/// por eso recorre el modelo leído en vez de reemplazar sobre el JSON. Un <c>{param.x}</c> sin valor queda literal.
/// </summary>
public static partial class TemplateParameters
{
    /// <summary>El flujo en la forma del modelo, con los parámetros de <paramref name="values"/> sustituidos.</summary>
    public static string Apply(string flowJson, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var flow = FlowJson.Parse(flowJson);
        string Text(string text) => ParamToken().Replace(text, match => values.TryGetValue(match.Groups["key"].Value, out var value) ? value : match.Value);
        string? Optional(string? text) => text is null ? null : Text(text);
        ListFixedRow Row(ListFixedRow row) => row with { Title = Text(row.Title), Description = Optional(row.Description) };

        FlowNode Node(FlowNode node) => node switch
        {
            MessageNode message => message with
            {
                Title = Text(message.Title),
                Text = Text(message.Text),
                UrlButton = message.UrlButton is { } button ? new UrlButton(Text(button.Label), Text(button.Url)) : null,
            },
            OpenQuestionNode question => question with
            {
                Title = Text(question.Title),
                Text = Text(question.Text),
                Validation = question.Validation with { Keywords = question.Validation.Keywords?.Select(Text).ToList() },
            },
            ButtonsNode buttons => buttons with
            {
                Title = Text(buttons.Title),
                Text = Text(buttons.Text),
                Buttons = [.. buttons.Buttons.Select(button => button with { Label = Text(button.Label) })],
            },
            ListNode list => list with
            {
                Title = Text(list.Title),
                Text = Text(list.Text),
                ButtonLabel = Text(list.ButtonLabel),
                Options = list.Options with
                {
                    Fixed = list.Options.Fixed?.Select(Row).ToList(),
                    ExtraOption = list.Options.ExtraOption is { } extra ? Row(extra) : null,
                },
            },
            AttachmentNode attachment => attachment with { Title = Text(attachment.Title), Text = Text(attachment.Text) },
            HandoffNode handoff => handoff with { Title = Text(handoff.Title), Text = Optional(handoff.Text) },
            ResolveNode resolve => resolve with { Title = Text(resolve.Title), Text = Optional(resolve.Text) },
            // Condición, horario y HTTP no tienen texto que el cliente lea: sólo el título del bloque.
            ConditionNode condition => condition with { Title = Text(condition.Title) },
            BusinessHoursNode hours => hours with { Title = Text(hours.Title) },
            HttpNode http => http with { Title = Text(http.Title) },
            _ => throw new ArgumentOutOfRangeException(nameof(node), node.Kind, "Bloque sin sustitución de parámetros definida."),
        };

        return FlowJson.Serialize(flow with { Nodes = [.. flow.Nodes.Select(Node)] });
    }

    /// <summary>El valor de cada parámetro que la plantilla declara: el que mandó el admin o, si no mandó, su valor por defecto. Una
    /// clave que la plantilla no declara se ignora: no hay dónde ponerla.</summary>
    public static IReadOnlyDictionary<string, string> Resolve(
        IReadOnlyList<TemplateParameter> parameters, IReadOnlyDictionary<string, string>? provided)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            values[parameter.Key] = provided is not null && provided.TryGetValue(parameter.Key, out var value) && value is not null
                ? value
                : parameter.DefaultValue;
        }

        return values;
    }

    [GeneratedRegex(@"\{param\.(?<key>[a-zA-Z0-9_]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex ParamToken();
}

/// <summary>Lo que la galería pinta de cada plantilla (§4.4).</summary>
internal static class TemplateCards
{
    // D-M6: el motor sólo atiende WhatsApp, así que toda plantilla del tenant es de WhatsApp.
    private static readonly string[] WhatsAppOnly = ["WhatsApp"];

    public static TemplateDto From(SystemTemplate template) => new(
        template.Id,
        template.Name,
        template.Category,
        template.Description,
        FlowJson.Parse(template.FlowJson).Nodes.Count,
        template.Channels,
        template.Requirement,
        OwnedByTenant: false,
        template.Preview,
        [.. template.Parameters.Select(parameter => new TemplateParameterDto(
            parameter.Key, parameter.Label, parameter.Kind, parameter.Options, parameter.DefaultValue))]);

    public static TemplateDto From(ChatbotTemplate template)
    {
        // El flujo se guardó en la forma del modelo; si algún día no se puede leer, la tarjeta sale sin vista previa en vez de tumbar la
        // galería entera.
        IReadOnlyList<string> preview;
        try
        {
            preview = [.. FlowJson.Parse(template.FlowJson).Nodes.Select(node => node.Title)];
        }
        catch (FlowFormatException)
        {
            preview = [];
        }

        return new TemplateDto(
            template.Id.ToString(), template.Name, template.Category, template.Description, preview.Count, WhatsAppOnly, null,
            OwnedByTenant: true, preview, []);
    }
}

public sealed class SaveChatbotAsTemplateValidator : AbstractValidator<SaveChatbotAsTemplateCommand>
{
    public SaveChatbotAsTemplateValidator()
    {
        RuleFor(command => command.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= ChatbotTemplate.NameMaxLength)
            .WithMessage($"Name must have between 1 and {ChatbotTemplate.NameMaxLength} characters.");
        RuleFor(command => command.Category)
            .Must(category => category is not null && ChatbotTemplateCategories.All.Contains(category))
            .WithMessage("Category must be Support, Sales, Orders or TechnicalService.");
        RuleFor(command => command.Description).NotNull().MaximumLength(ChatbotTemplate.DescriptionMaxLength);
    }
}

public sealed class ListChatbotTemplatesHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, ISystemTemplateCatalog catalog, IChatbotTemplateRepository templates)
    : IQueryHandler<ListChatbotTemplatesQuery, TemplateCatalogDto>
{
    public async Task<TemplateCatalogDto> HandleAsync(ListChatbotTemplatesQuery query, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AutomationsPermissions.ChatbotRead, cancellationToken);
        var mine = await templates.ListAsync(query.TenantId, cancellationToken);
        return new TemplateCatalogDto([.. catalog.All.Select(TemplateCards.From)], [.. mine.Select(TemplateCards.From)]);
    }
}

/// <summary>
/// D-C8: copia el borrador (flujo y posiciones) a una plantilla del tenant; nada queda enlazado al chatbot. D-C14: una plantilla nunca
/// referencia conexiones, así que los bloques HTTP se guardan con <c>connectionId: null</c> y Validar los marca
/// <c>http.connection_missing</c> en el chatbot que se cree desde ella. El nombre repetido lo responde el índice
/// (<c>IX_chatbot_templates_tenant_name</c>) con 409 <c>automations.template.name_taken</c>.
/// </summary>
public sealed class SaveChatbotAsTemplateHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    AutomationsCaller caller,
    IChatbotRepository chatbots,
    IChatbotTemplateRepository templates,
    IAutomationsUnitOfWork unitOfWork,
    IAutomationsAuditRecorder audit,
    IClock clock,
    IValidator<SaveChatbotAsTemplateCommand> validator)
    : ICommandHandler<SaveChatbotAsTemplateCommand, TemplateDto>
{
    public async Task<TemplateDto> HandleAsync(SaveChatbotAsTemplateCommand command, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var chatbot = await chatbots.FindAsync(command.TenantId, command.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(command.ChatbotId);
        var by = await caller.RequireAsync(command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        var template = ChatbotTemplate.Create(
            command.TenantId, command.Name, command.Category, command.Description, WithoutConnections(chatbot.DraftFlowJson),
            chatbot.DraftLayoutJson, by, now);
        await templates.AddAsync(template, cancellationToken);
        audit.Record(command.TenantId, executionContext.SubjectId, false, AutomationsAuditActions.TemplateCreated, AutomationsAuditActions.ResourceTemplate, template.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return TemplateCards.From(template);
    }

    /// <summary>Un borrador que no se puede leer no se puede volver plantilla: el mismo 422 con el código bajo <c>flow</c> que guardarlo.</summary>
    private static string WithoutConnections(string flowJson)
    {
        FlowDefinition flow;
        try
        {
            flow = FlowJson.Parse(flowJson);
        }
        catch (FlowFormatException exception)
        {
            throw new ValidationException([new ValidationFailure("flow", exception.Code)]);
        }

        return FlowJson.Serialize(flow with
        {
            Nodes = [.. flow.Nodes.Select(node => node is HttpNode http ? http with { ConnectionId = null } : node)],
        });
    }
}

/// <summary>§4.4: sólo las del tenant. Un id del sistema (o un Guid que no es de este tenant) es 404 <c>not_found</c>, sin decir de
/// quién es.</summary>
public sealed class DeleteChatbotTemplateHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IChatbotTemplateRepository templates,
    IAutomationsUnitOfWork unitOfWork,
    IAutomationsAuditRecorder audit,
    IClock clock)
    : ICommandHandler<DeleteChatbotTemplateCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteChatbotTemplateCommand command, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        var template = Guid.TryParse(command.TemplateId, out var id) ? await templates.FindAsync(command.TenantId, id, cancellationToken) : null;
        if (template is null)
        {
            throw AutomationsNotFound.Template(command.TemplateId);
        }

        audit.Record(command.TenantId, executionContext.SubjectId, false, AutomationsAuditActions.TemplateDeleted, AutomationsAuditActions.ResourceTemplate, template.Id, clock.UtcNow);
        templates.Remove(template);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
