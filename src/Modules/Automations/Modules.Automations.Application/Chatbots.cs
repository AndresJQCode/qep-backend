using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Flow;
using Modules.Tenancy.Application;

namespace Modules.Automations.Application;

// Spec 2026-10-21 §4.4: lista, creación (en blanco, desde una plantilla o como duplicado), lectura, edición, borrador, validación,
// publicación, pausa, reanudación, duplicado y borrado del chatbot.

public sealed record ListChatbotsQuery(Guid TenantId, string? Search) : IQuery<ChatbotListResponse>;

public sealed record GetChatbotQuery(Guid TenantId, Guid ChatbotId) : IQuery<ChatbotEditorDto>;

/// <summary><see cref="Kind"/> es <c>blank</c>, <c>template</c> (con <see cref="TemplateId"/>: el id de una del sistema o el Guid de
/// una del tenant, y <see cref="Parameters"/> opcionales, D-C14) o <c>duplicate</c> (con <see cref="ChatbotId"/>) (§4.4).</summary>
public sealed record ChatbotSource(string Kind, string? TemplateId, Guid? ChatbotId, IReadOnlyDictionary<string, string>? Parameters)
{
    public const string Blank = "blank";
    public const string Template = "template";
    public const string Duplicate = "duplicate";
}

public sealed record CreateChatbotCommand(
    Guid TenantId, string Name, string Description, string Trigger, IReadOnlyList<string> Keywords, IReadOnlyList<Guid> ConnectionIds,
    ChatbotSource Source) : ICommand<ChatbotEditorDto>;

public sealed record UpdateChatbotCommand(
    Guid TenantId, Guid ChatbotId, long Version, string Name, string Description, string Trigger, IReadOnlyList<string> Keywords,
    string RestartKeyword, int MaxAttempts, IReadOnlyList<Guid> ConnectionIds) : ICommand<ChatbotEditorDto>;

public sealed record SaveChatbotDraftCommand(Guid TenantId, Guid ChatbotId, long Version, JsonElement Flow, JsonElement Layout)
    : ICommand<DraftSavedDto>;

public sealed record ValidateChatbotQuery(Guid TenantId, Guid ChatbotId) : IQuery<IReadOnlyList<FlowIssueDto>>;

public sealed record PublishChatbotCommand(Guid TenantId, Guid ChatbotId, long Version) : ICommand<ChatbotEditorDto>;

public sealed record PauseChatbotCommand(Guid TenantId, Guid ChatbotId, long Version) : ICommand<ChatbotEditorDto>;

public sealed record ResumeChatbotCommand(Guid TenantId, Guid ChatbotId, long Version) : ICommand<ChatbotEditorDto>;

public sealed record DuplicateChatbotCommand(Guid TenantId, Guid ChatbotId) : ICommand<ChatbotEditorDto>;

public sealed record DeleteChatbotCommand(Guid TenantId, Guid ChatbotId, long Version) : ICommand<bool>;

public sealed record ExportChatbotQuery(Guid TenantId, Guid ChatbotId) : IQuery<ChatbotExportDto>;

public sealed record ImportChatbotCommand(Guid TenantId, ImportChatbotRequest Request) : ICommand<ChatbotEditorDto>;

/// <summary>Los límites del cuerpo (§4.4) y de los campos que comparten crear y editar.</summary>
public static class ChatbotLimits
{
    /// <summary>§4.4: el borrador pesa a lo sumo 512 KB serializado, en bytes UTF-8 (lo que viaja y lo que guarda el jsonb).</summary>
    public const int FlowMaxLength = 512 * 1024;

    /// <summary>Las posiciones no se validan (D-C2), pero no pueden crecer sin tope. En bytes UTF-8.</summary>
    public const int LayoutMaxLength = 256 * 1024;

    /// <summary>El tope del cuerpo de <c>PUT /draft</c>, antes de leerlo: flujo, posiciones y margen para el sobre JSON.</summary>
    public const int DraftBodyMaxBytes = 800 * 1024;

    /// <summary>El tope del cuerpo de <c>POST /import</c>. Más que el del borrador a propósito: un export con flujo y posiciones en sus
    /// topes (512 + 256 KB) más nombre, palabras clave y el mapa de conexiones se pasa de 800 KB, y lo que se exporta tiene que poder
    /// volver a importarse.</summary>
    public const int ImportBodyMaxBytes = 1024 * 1024;

    /// <summary>Brief de la tarea 14: cuántas conexiones puede atender un chatbot.</summary>
    public const int MaxConnections = 10;

    /// <summary>D-C15: cuántos pares de conexión puede traer una importación (un flujo no usa tantas «API externa»).</summary>
    public const int MaxConnectionMapEntries = 50;

    /// <summary>El flujo de un chatbot recién creado en blanco y sus posiciones (brief de la tarea 14).</summary>
    public const string BlankFlow = """{"schema":1,"start":{"firstNodeId":null},"variables":[],"nodes":[]}""";

    public const string BlankLayout = """{"positions":{}}""";

    /// <summary>D-C12: hasta «(copia 50)». Más allá es un nombre que el tenant tiene que elegir a mano (409 <c>name_taken</c>).</summary>
    public const int MaxCopyNumber = 50;

    /// <summary>D-C14: cuántos parámetros de plantilla y de qué largo. Un valor va a parar a un texto del flujo, así que no puede pasar
    /// del cuerpo de un interactivo de Meta (1024, D-I1); lo que quede largo para su campo lo marca Validar.</summary>
    public const int MaxTemplateParameters = 20;

    public const int TemplateParameterMaxLength = 1024;

    /// <summary>El largo de un id de plantilla (del sistema o Guid): más es un id que no existe.</summary>
    public const int TemplateIdMaxLength = 64;
}

/// <summary>Las reglas que comparten crear y editar. Texto libre ⇒ validador aunque el agregado valide (CLAUDE.md): el validador da
/// el campo; y así <c>MaxAttempts</c> fuera de 1..5 nunca llega al <c>ArgumentOutOfRangeException</c> del agregado (500), ni una
/// descripción larga a su recorte silencioso.</summary>
internal static class ChatbotRules
{
    // `key` renombra la clave del error (importar los pone bajo «export.»): el handler de crear y editar la deja como está.
    public static void Name<T>(IRuleBuilderInitial<T, string> rule, string? key = null) =>
        Keyed(rule.NotEmpty().MaximumLength(Chatbot.NameMaxLength), key);

    public static void Description<T>(IRuleBuilderInitial<T, string> rule, string? key = null) =>
        Keyed(rule.NotNull().MaximumLength(Chatbot.DescriptionMaxLength), key);

    public static void Trigger<T>(IRuleBuilderInitial<T, string> rule, string? key = null) =>
        Keyed(rule.Must(trigger => TryParseTrigger(trigger, out _)).WithMessage("Trigger must be NewConversation, Keyword or OutsideHours."), key);

    public static void Keywords<T>(IRuleBuilderInitial<T, IReadOnlyList<string>> rule, string? key = null) =>
        Keyed(
            rule.NotNull()
                .Must(keywords => keywords.Count <= Chatbot.MaxKeywords)
                .WithMessage($"At most {Chatbot.MaxKeywords} keywords are allowed.")
                .Must(keywords => keywords.All(keyword => keyword is not null && keyword.Trim().Length <= Chatbot.KeywordMaxLength))
                .WithMessage($"Each keyword can have at most {Chatbot.KeywordMaxLength} characters."),
            key);

    public static void ConnectionIds<T>(IRuleBuilderInitial<T, IReadOnlyList<Guid>> rule) =>
        rule.NotNull()
            .Must(ids => ids.Count <= ChatbotLimits.MaxConnections)
            .WithMessage($"At most {ChatbotLimits.MaxConnections} connections are allowed.")
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .WithMessage("A connection cannot be repeated.");

    private static void Keyed<T, TProperty>(IRuleBuilderOptions<T, TProperty> rule, string? key)
    {
        if (key is not null)
        {
            rule.OverridePropertyName(key);
        }
    }

    public static bool TryParseTrigger(string? value, out ChatbotTrigger trigger)
    {
        trigger = default;
        return value is not null && Enum.TryParse(value, ignoreCase: false, out trigger) && Enum.IsDefined(trigger)
            && !int.TryParse(value, out _);
    }
}

public sealed class CreateChatbotValidator : AbstractValidator<CreateChatbotCommand>
{
    public CreateChatbotValidator()
    {
        ChatbotRules.Name(RuleFor(command => command.Name));
        ChatbotRules.Description(RuleFor(command => command.Description));
        ChatbotRules.Trigger(RuleFor(command => command.Trigger));
        ChatbotRules.Keywords(RuleFor(command => command.Keywords));
        ChatbotRules.ConnectionIds(RuleFor(command => command.ConnectionIds));
        RuleFor(command => command.Source).NotNull();
        When(command => command.Source is not null, () =>
        {
            RuleFor(command => command.Source.Kind)
                .Must(kind => kind is ChatbotSource.Blank or ChatbotSource.Template or ChatbotSource.Duplicate)
                .WithMessage("Source kind must be blank, template or duplicate.");
            RuleFor(command => command.Source.TemplateId)
                .NotEmpty()
                .MaximumLength(ChatbotLimits.TemplateIdMaxLength)
                .When(command => command.Source.Kind == ChatbotSource.Template);
            RuleFor(command => command.Source.ChatbotId)
                .NotNull()
                .When(command => command.Source.Kind == ChatbotSource.Duplicate);
            RuleFor(command => command.Source.Parameters)
                .Must(parameters => parameters!.Count <= ChatbotLimits.MaxTemplateParameters)
                .WithMessage($"At most {ChatbotLimits.MaxTemplateParameters} template parameters are allowed.")
                .Must(parameters => parameters!.Values.All(value => value is null || value.Length <= ChatbotLimits.TemplateParameterMaxLength))
                .WithMessage($"Each template parameter can have at most {ChatbotLimits.TemplateParameterMaxLength} characters.")
                .When(command => command.Source.Parameters is not null);
        });
    }
}

public sealed class UpdateChatbotValidator : AbstractValidator<UpdateChatbotCommand>
{
    public UpdateChatbotValidator()
    {
        ChatbotRules.Name(RuleFor(command => command.Name));
        ChatbotRules.Description(RuleFor(command => command.Description));
        ChatbotRules.Trigger(RuleFor(command => command.Trigger));
        ChatbotRules.Keywords(RuleFor(command => command.Keywords));
        RuleFor(command => command.RestartKeyword).NotEmpty().MaximumLength(Chatbot.RestartKeywordMaxLength);
        RuleFor(command => command.MaxAttempts).InclusiveBetween(Chatbot.MinAttempts, Chatbot.MaxAttemptsLimit);
        ChatbotRules.ConnectionIds(RuleFor(command => command.ConnectionIds));
    }
}

/// <summary>§4.4: el cuerpo pesa a lo sumo 512 KB (flujo) y 256 KB (posiciones). Que el flujo se pueda leer lo dice el handler, con
/// el código de <see cref="FlowFormatException"/> bajo la misma clave <c>flow</c>.</summary>
public sealed class SaveChatbotDraftValidator : AbstractValidator<SaveChatbotDraftCommand>
{
    public SaveChatbotDraftValidator()
    {
        RuleFor(command => command.Flow)
            .Cascade(CascadeMode.Stop)
            .Must(flow => flow.ValueKind == JsonValueKind.Object)
            .WithMessage(FlowFormatException.Malformed)
            .Must(flow => Encoding.UTF8.GetByteCount(flow.GetRawText()) <= ChatbotLimits.FlowMaxLength)
            .WithMessage($"The flow cannot exceed {ChatbotLimits.FlowMaxLength / 1024} KB.")
            .OverridePropertyName("flow");
        RuleFor(command => command.Layout)
            .Cascade(CascadeMode.Stop)
            .Must(layout => layout.ValueKind == JsonValueKind.Object)
            .WithMessage("The layout must be a JSON object.")
            .Must(layout => Encoding.UTF8.GetByteCount(layout.GetRawText()) <= ChatbotLimits.LayoutMaxLength)
            .WithMessage($"The layout cannot exceed {ChatbotLimits.LayoutMaxLength / 1024} KB.")
            .OverridePropertyName("layout");
    }
}

/// <summary>D-C9: el archivo importado se valida con los mismos límites del borrador (512 KB el flujo, 256 KB las posiciones) y las
/// mismas reglas de campos que crear y editar. Que el flujo se pueda leer lo dice el handler bajo <c>export.flow</c>, como
/// <see cref="SaveChatbotDraftValidator"/> bajo <c>flow</c>.</summary>
public sealed class ImportChatbotValidator : AbstractValidator<ImportChatbotCommand>
{
    private static string EffectiveName(ImportChatbotRequest request) => request.Name ?? request.Export?.Name ?? string.Empty;

    public ImportChatbotValidator()
    {
        RuleFor(command => command.Request).NotNull();
        When(command => command.Request is not null, () =>
        {
            RuleFor(command => command.Request.Export).NotNull().OverridePropertyName("export");
            RuleFor(command => command.Request.ConnectionMap)
                .Must(map => map!.Count <= ChatbotLimits.MaxConnectionMapEntries)
                .WithMessage($"At most {ChatbotLimits.MaxConnectionMapEntries} connection mappings are allowed.")
                .When(command => command.Request.ConnectionMap is not null)
                .OverridePropertyName("connectionMap");
            // El nombre que se usa es el del pedido si viene; si no, el del archivo.
            ChatbotRules.Name(RuleFor(command => EffectiveName(command.Request)), "name");
            When(command => command.Request.Export is not null, () =>
            {
                RuleFor(command => command.Request.Export.Schema)
                    .Equal(ChatbotExportDto.CurrentSchema).OverridePropertyName("export.schema");
                ChatbotRules.Description(RuleFor(command => command.Request.Export.Description), "export.description");
                ChatbotRules.Trigger(RuleFor(command => command.Request.Export.Trigger), "export.trigger");
                ChatbotRules.Keywords(RuleFor(command => command.Request.Export.Keywords), "export.keywords");
                RuleFor(command => command.Request.Export.RestartKeyword)
                    .NotEmpty().MaximumLength(Chatbot.RestartKeywordMaxLength).OverridePropertyName("export.restartKeyword");
                RuleFor(command => command.Request.Export.MaxAttempts)
                    .InclusiveBetween(Chatbot.MinAttempts, Chatbot.MaxAttemptsLimit).OverridePropertyName("export.maxAttempts");
                RuleFor(command => command.Request.Export.Flow)
                    .Cascade(CascadeMode.Stop)
                    .Must(flow => flow.ValueKind == JsonValueKind.Object)
                    .WithMessage(FlowFormatException.Malformed)
                    .Must(flow => Encoding.UTF8.GetByteCount(flow.GetRawText()) <= ChatbotLimits.FlowMaxLength)
                    .WithMessage($"The flow cannot exceed {ChatbotLimits.FlowMaxLength / 1024} KB.")
                    .OverridePropertyName("export.flow");
                RuleFor(command => command.Request.Export.Layout)
                    .Cascade(CascadeMode.Stop)
                    .Must(layout => layout.ValueKind == JsonValueKind.Object)
                    .WithMessage("The layout must be a JSON object.")
                    .Must(layout => Encoding.UTF8.GetByteCount(layout.GetRawText()) <= ChatbotLimits.LayoutMaxLength)
                    .WithMessage($"The layout cannot exceed {ChatbotLimits.LayoutMaxLength / 1024} KB.")
                    .OverridePropertyName("export.layout");
            });
        });
    }
}

/// <summary>D-M6 y la tarea 14 del plan: cada conexión tiene que ser del tenant (404 si no, sin decir de quién es) y de WhatsApp.</summary>
internal static class ChatbotConnectionCheck
{
    private const string WhatsApp = "WhatsApp";

    public static void Ensure(IReadOnlyList<AutomationConnectionRef> tenantConnections, IReadOnlyList<Guid> connectionIds)
    {
        var byId = tenantConnections.ToDictionary(connection => connection.Id);
        foreach (var id in connectionIds)
        {
            if (!byId.TryGetValue(id, out var connection))
            {
                throw AutomationsNotFound.Connection(id);
            }

            if (connection.Channel != WhatsApp)
            {
                throw new AutomationsDomainException(
                    AutomationsErrorCodes.ChatbotChannelNotSupported, "Por ahora un chatbot sólo puede atender conexiones de WhatsApp.");
            }
        }
    }

    public static async Task EnsureNameIsFreeAsync(
        IChatbotRepository chatbots, Guid tenantId, string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        // El índice único (IX_chatbots_tenant_name) es el respaldo ante dos creaciones simultáneas; esto da el 409 sin llegar a él.
        if (await chatbots.NameExistsAsync(tenantId, name, exceptId, cancellationToken))
        {
            throw new ResourceConflictException(AutomationsErrorCodes.ChatbotNameTaken, "Ya existe un chatbot con ese nombre.");
        }
    }

    /// <summary>
    /// D-C4: el 409 <c>connection_taken</c> lo decide el índice parcial (<c>IX_chatbot_connections_active</c>) al guardar, no un chequeo
    /// previo: dos publicaciones simultáneas lo pasarían las dos. Sólo cuando choca se busca quién la ocupa, para que el mensaje lo
    /// nombre («con el nombre del que la ocupa»); la publicación que ganó ya está confirmada, así que la proyección la ve.
    /// </summary>
    public static async Task SaveNamingTheHolderAsync(
        IAutomationsUnitOfWork unitOfWork, IChatbotRepository chatbots, Chatbot chatbot, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ResourceConflictException exception) when (exception.Code == AutomationsErrorCodes.ChatbotConnectionTaken)
        {
            var own = chatbot.Connections.Select(connection => connection.ConnectionId).ToHashSet();
            var holder = (await chatbots.ListServingConnectionsAsync(chatbot.TenantId, cancellationToken))
                .FirstOrDefault(serving => serving.ChatbotId != chatbot.Id && own.Contains(serving.ConnectionId));
            if (holder is null)
            {
                throw;
            }

            throw new ResourceConflictException(
                AutomationsErrorCodes.ChatbotConnectionTaken, $"La conexión ya la atiende el chatbot «{holder.Name}».", exception);
        }
    }
}

public sealed class ListChatbotsHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IChatbotRepository chatbots, ChatbotEditorBuilder builder)
    : IQueryHandler<ListChatbotsQuery, ChatbotListResponse>
{
    public async Task<ChatbotListResponse> HandleAsync(ListChatbotsQuery query, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AutomationsPermissions.ChatbotRead, cancellationToken);
        var rows = await chatbots.ListSummariesAsync(query.TenantId, query.Search, cancellationToken);
        return await builder.BuildListAsync(query.TenantId, rows, cancellationToken);
    }
}

public sealed class GetChatbotHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IChatbotRepository chatbots, ChatbotEditorBuilder builder)
    : IQueryHandler<GetChatbotQuery, ChatbotEditorDto>
{
    public async Task<ChatbotEditorDto> HandleAsync(GetChatbotQuery query, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AutomationsPermissions.ChatbotRead, cancellationToken);
        var chatbot = await chatbots.FindAsync(query.TenantId, query.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(query.ChatbotId);
        return await builder.BuildAsync(chatbot, cancellationToken);
    }
}

/// <summary>
/// Nace en borrador (D-C1) con sus conexiones inactivas: un borrador no atiende a nadie (D-C13). El flujo y las posiciones salen de
/// <c>source</c>: en blanco; de una plantilla del sistema con sus parámetros sustituidos (D-C14) o de una del tenant tal cual; o del
/// borrador de otro chatbot, como <c>POST /duplicate</c> pero con el nombre, la descripción, el disparador, las palabras y las
/// conexiones del pedido (D-C8: nada queda enlazado al origen).
/// </summary>
public sealed class CreateChatbotHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    AutomationsCaller caller,
    IChatbotRepository chatbots,
    IChatbotTemplateRepository templates,
    ISystemTemplateCatalog systemTemplates,
    IAutomationConnections connections,
    IAutomationsUnitOfWork unitOfWork,
    IAutomationsAuditRecorder audit,
    IClock clock,
    IValidator<CreateChatbotCommand> validator,
    ChatbotEditorBuilder builder)
    : ICommandHandler<CreateChatbotCommand, ChatbotEditorDto>
{
    public async Task<ChatbotEditorDto> HandleAsync(CreateChatbotCommand command, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var by = await caller.RequireAsync(command.TenantId, cancellationToken);
        var (flowJson, layoutJson, source) = await ResolveSourceAsync(command.TenantId, command.Source, cancellationToken);
        var tenantConnections = await connections.ListAsync(command.TenantId, cancellationToken);
        ChatbotConnectionCheck.Ensure(tenantConnections, command.ConnectionIds);
        await ChatbotConnectionCheck.EnsureNameIsFreeAsync(chatbots, command.TenantId, command.Name, null, cancellationToken);

        var now = clock.UtcNow;
        // El validador ya garantizó el disparador: esta conversión no lanza.
        var trigger = Enum.Parse<ChatbotTrigger>(command.Trigger);
        var chatbot = Chatbot.Create(command.TenantId, command.Name, command.Description, trigger, command.Keywords, flowJson, layoutJson, by, now);
        if (source is not null)
        {
            // Como POST /duplicate (D-C12): la palabra de reinicio y el tope de intentos vienen con el borrador; lo demás, del pedido.
            chatbot.UpdateSettings(trigger, command.Keywords, source.RestartKeyword, source.MaxAttempts, now);
        }

        chatbot.SetConnections(command.ConnectionIds, now);
        await chatbots.AddAsync(chatbot, cancellationToken);
        audit.Record(command.TenantId, executionContext.SubjectId, false, AutomationsAuditActions.ChatbotCreated, AutomationsAuditActions.ResourceChatbot, chatbot.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await builder.BuildAsync(chatbot, tenantConnections, cancellationToken);
    }

    /// <summary>El flujo y las posiciones del chatbot nuevo, y el chatbot de origen si es un duplicado. Una plantilla que no existe es 404
    /// <c>automations.template.not_found</c>; un chatbot de origen que no existe, 404 <c>automations.chatbot.not_found</c>.</summary>
    private async Task<(string FlowJson, string LayoutJson, Chatbot? Source)> ResolveSourceAsync(
        Guid tenantId, ChatbotSource source, CancellationToken cancellationToken)
    {
        switch (source.Kind)
        {
            case ChatbotSource.Template:
                var templateId = source.TemplateId!;
                if (systemTemplates.Find(templateId) is { } system)
                {
                    var values = TemplateParameters.Resolve(system.Parameters, source.Parameters);
                    return (TemplateParameters.Apply(system.FlowJson, values), system.LayoutJson, null);
                }

                // Una plantilla del tenant no declara parámetros (D-C14): se copia tal cual.
                var own = Guid.TryParse(templateId, out var id) ? await templates.FindAsync(tenantId, id, cancellationToken) : null;
                return own is not null ? (own.FlowJson, own.LayoutJson, null) : throw AutomationsNotFound.Template(templateId);
            case ChatbotSource.Duplicate:
                var chatbotId = source.ChatbotId!.Value;
                var original = await chatbots.FindAsync(tenantId, chatbotId, cancellationToken) ?? throw AutomationsNotFound.Chatbot(chatbotId);
                return (original.DraftFlowJson, original.DraftLayoutJson, original);
            default:
                return (ChatbotLimits.BlankFlow, ChatbotLimits.BlankLayout, null);
        }
    }
}

/// <summary>Nombre, descripción, disparador, palabras, reinicio, intentos y conexiones, todo junto (§4.4). En un chatbot publicado las
/// conexiones nuevas nacen ocupadas, y si otro activo ya tiene una el índice responde 409 <c>connection_taken</c>.</summary>
public sealed class UpdateChatbotHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IChatbotRepository chatbots,
    IAutomationConnections connections,
    IAutomationsUnitOfWork unitOfWork,
    IAutomationsAuditRecorder audit,
    IClock clock,
    IValidator<UpdateChatbotCommand> validator,
    ChatbotEditorBuilder builder)
    : ICommandHandler<UpdateChatbotCommand, ChatbotEditorDto>
{
    public async Task<ChatbotEditorDto> HandleAsync(UpdateChatbotCommand command, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var chatbot = await chatbots.FindAsync(command.TenantId, command.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(command.ChatbotId);
        AutomationsVersions.Ensure(chatbot.Version, command.Version);
        var tenantConnections = await connections.ListAsync(command.TenantId, cancellationToken);
        ChatbotConnectionCheck.Ensure(tenantConnections, command.ConnectionIds);
        await ChatbotConnectionCheck.EnsureNameIsFreeAsync(chatbots, command.TenantId, command.Name, chatbot.Id, cancellationToken);

        var now = clock.UtcNow;
        chatbot.Rename(command.Name, command.Description, now);
        chatbot.UpdateSettings(Enum.Parse<ChatbotTrigger>(command.Trigger), command.Keywords, command.RestartKeyword, command.MaxAttempts, now);
        chatbot.SetConnections(command.ConnectionIds, now);
        audit.Record(command.TenantId, executionContext.SubjectId, false, AutomationsAuditActions.ChatbotUpdated, AutomationsAuditActions.ResourceChatbot, chatbot.Id, now);
        await ChatbotConnectionCheck.SaveNamingTheHolderAsync(unitOfWork, chatbots, chatbot, cancellationToken);

        return await builder.BuildAsync(chatbot, tenantConnections, cancellationToken);
    }
}

/// <summary>D-C3: guarda siempre que el flujo se pueda leer, aunque tenga errores, y responde la lista de problemas. D-C2: se guarda en
/// la forma del modelo (<see cref="FlowJson.Serialize"/>), que es lo que el editor vuelve a dibujar; el <c>layout</c>, tal cual.</summary>
public sealed class SaveChatbotDraftHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    AutomationsCaller caller,
    IChatbotRepository chatbots,
    IAutomationsUnitOfWork unitOfWork,
    IAutomationsAuditRecorder audit,
    IAutomationsAuditQueries auditQueries,
    IClock clock,
    IValidator<SaveChatbotDraftCommand> validator,
    ChatbotEditorBuilder builder)
    : ICommandHandler<SaveChatbotDraftCommand, DraftSavedDto>
{
    /// <summary>§4.6: «draft_saved» a lo sumo una vez por minuto por chatbot.</summary>
    private static readonly TimeSpan DraftAuditInterval = TimeSpan.FromSeconds(60);

    public async Task<DraftSavedDto> HandleAsync(SaveChatbotDraftCommand command, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var flow = Parse(command.Flow);
        var chatbot = await chatbots.FindAsync(command.TenantId, command.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(command.ChatbotId);
        AutomationsVersions.Ensure(chatbot.Version, command.Version);
        var by = await caller.RequireAsync(command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        chatbot.SaveDraft(FlowJson.Serialize(flow), command.Layout.GetRawText(), by, now);
        // §4.6: el editor guarda solo cada pocos segundos; una entrada por guardado inundaría la auditoría. Una por minuto y chatbot.
        if (!await auditQueries.HasDraftSavedSinceAsync(command.TenantId, chatbot.Id, now - DraftAuditInterval, cancellationToken))
        {
            audit.Record(command.TenantId, executionContext.SubjectId, false, AutomationsAuditActions.ChatbotDraftSaved, AutomationsAuditActions.ResourceChatbot, chatbot.Id, now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new DraftSavedDto(chatbot.Version, chatbot.DraftUpdatedAt, await builder.ValidateAsync(chatbot, flow, cancellationToken));
    }

    /// <summary>Lo que no es un flujo (un <c>kind</c> que no existe, un campo de lista cerrada con otro valor) es un 422 de validación
    /// con el código bajo <c>flow</c>, no un problema del validador: no hay qué guardar.</summary>
    private static FlowDefinition Parse(JsonElement flow)
    {
        try
        {
            return FlowJson.Parse(flow.GetRawText());
        }
        catch (FlowFormatException exception)
        {
            throw FlowFormatFailures.For("flow", exception);
        }
    }
}

/// <summary>Sin efectos: la misma validación que trae <c>GET /chatbots/{id}</c>.</summary>
public sealed class ValidateChatbotHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IChatbotRepository chatbots, ChatbotEditorBuilder builder)
    : IQueryHandler<ValidateChatbotQuery, IReadOnlyList<FlowIssueDto>>
{
    public async Task<IReadOnlyList<FlowIssueDto>> HandleAsync(ValidateChatbotQuery query, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AutomationsPermissions.ChatbotRead, cancellationToken);
        var chatbot = await chatbots.FindAsync(query.TenantId, query.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(query.ChatbotId);
        return await builder.ValidateStoredAsync(chatbot, cancellationToken);
    }
}

/// <summary>
/// D-C3: valida el borrador guardado con el contexto real (el mismo camino que <c>/validate</c>); un error responde 422
/// <c>automations.chatbot.invalid</c> con los errores bajo <c>errors.flow</c> como «código: mensaje». Los avisos no bloquean ni viajan
/// ahí: el editor ya los tiene en <c>issues</c>. D-C13: publicar ocupa las conexiones del chatbot y el 409 <c>connection_taken</c> lo da
/// el índice al guardar.
/// </summary>
public sealed class PublishChatbotHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    AutomationsCaller caller,
    IChatbotRepository chatbots,
    IAutomationConnections connections,
    IAutomationsUnitOfWork unitOfWork,
    IAutomationsAuditRecorder audit,
    IClock clock,
    ChatbotEditorBuilder builder)
    : ICommandHandler<PublishChatbotCommand, ChatbotEditorDto>
{
    private const string FlowField = "flow";

    public async Task<ChatbotEditorDto> HandleAsync(PublishChatbotCommand command, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        var chatbot = await chatbots.FindAsync(command.TenantId, command.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(command.ChatbotId);
        AutomationsVersions.Ensure(chatbot.Version, command.Version);
        var by = await caller.RequireAsync(command.TenantId, cancellationToken);
        var tenantConnections = await connections.ListAsync(command.TenantId, cancellationToken);

        var issues = (await builder.ValidateStoredAsync(chatbot, tenantConnections, cancellationToken)).ToList();
        // Es un problema de los ajustes y no de un bloque, así que no lo ve el validador del flujo; guardar los ajustes sí se permite.
        if (chatbot.Trigger == ChatbotTrigger.Keyword && chatbot.Keywords.Count == 0)
        {
            issues.Add(new FlowIssueDto(
                nameof(FlowIssueSeverity.Error), FlowIssueCodes.TriggerKeywordsEmpty, null,
                "El chatbot arranca por palabra clave y no tiene ninguna: agrega al menos una en los ajustes."));
        }

        var errors = issues.Where(issue => issue.Severity == nameof(FlowIssueSeverity.Error)).ToList();
        if (errors.Count > 0)
        {
            throw new AutomationsDomainException(
                AutomationsErrorCodes.ChatbotInvalid,
                "El flujo tiene errores: corrígelos para publicar.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [FlowField] = [.. errors.Select(error => $"{error.Code}: {error.Message}")],
                });
        }

        var now = clock.UtcNow;
        // El agregado vuelve a exigir cero errores: es la regla, y esto sólo adelanta el 422 con la lista.
        chatbot.Publish([.. issues.Select(ToDomain)], by, now);
        audit.Record(command.TenantId, executionContext.SubjectId, false, AutomationsAuditActions.ChatbotPublished, AutomationsAuditActions.ResourceChatbot, chatbot.Id, now);
        await ChatbotConnectionCheck.SaveNamingTheHolderAsync(unitOfWork, chatbots, chatbot, cancellationToken);

        return await builder.BuildAsync(chatbot, tenantConnections, cancellationToken);
    }

    private static FlowIssue ToDomain(FlowIssueDto issue) =>
        new(Enum.Parse<FlowIssueSeverity>(issue.Severity), issue.Code, issue.NodeId, issue.Message);
}

/// <summary>D-C5: deja de entrar a conversaciones nuevas; las corridas en curso terminan. D-C13: la conexión se conserva. Un borrador
/// es 422 <c>not_published</c> (lo dice el agregado).</summary>
public sealed class PauseChatbotHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IChatbotRepository chatbots,
    IAutomationsUnitOfWork unitOfWork,
    IAutomationsAuditRecorder audit,
    IClock clock,
    ChatbotEditorBuilder builder)
    : ICommandHandler<PauseChatbotCommand, ChatbotEditorDto>
{
    public async Task<ChatbotEditorDto> HandleAsync(PauseChatbotCommand command, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        var chatbot = await chatbots.FindAsync(command.TenantId, command.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(command.ChatbotId);
        AutomationsVersions.Ensure(chatbot.Version, command.Version);

        var now = clock.UtcNow;
        chatbot.Pause(now);
        audit.Record(command.TenantId, executionContext.SubjectId, false, AutomationsAuditActions.ChatbotPaused, AutomationsAuditActions.ResourceChatbot, chatbot.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await builder.BuildAsync(chatbot, cancellationToken);
    }
}

/// <summary>D-C5: vuelve a <c>Active</c> con la versión publicada que tenía. Las conexiones nunca se soltaron (D-C13), así que no hay
/// nada que volver a ocupar.</summary>
public sealed class ResumeChatbotHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IChatbotRepository chatbots,
    IAutomationsUnitOfWork unitOfWork,
    IAutomationsAuditRecorder audit,
    IClock clock,
    ChatbotEditorBuilder builder)
    : ICommandHandler<ResumeChatbotCommand, ChatbotEditorDto>
{
    public async Task<ChatbotEditorDto> HandleAsync(ResumeChatbotCommand command, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        var chatbot = await chatbots.FindAsync(command.TenantId, command.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(command.ChatbotId);
        AutomationsVersions.Ensure(chatbot.Version, command.Version);

        var now = clock.UtcNow;
        chatbot.Resume(now);
        audit.Record(command.TenantId, executionContext.SubjectId, false, AutomationsAuditActions.ChatbotResumed, AutomationsAuditActions.ResourceChatbot, chatbot.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await builder.BuildAsync(chatbot, cancellationToken);
    }
}

/// <summary>§4.4 y D-C12: copia el borrador (flujo y posiciones) y los ajustes (disparador, palabras, reinicio, intentos) a un chatbot
/// nuevo en <c>Draft</c>, sin conexiones, llamado «{nombre} (copia)», «(copia 2)», … Sin <c>If-Match</c>: no cambia el original.</summary>
public sealed class DuplicateChatbotHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    AutomationsCaller caller,
    IChatbotRepository chatbots,
    IAutomationsUnitOfWork unitOfWork,
    IAutomationsAuditRecorder audit,
    IClock clock,
    ChatbotEditorBuilder builder)
    : ICommandHandler<DuplicateChatbotCommand, ChatbotEditorDto>
{
    public async Task<ChatbotEditorDto> HandleAsync(DuplicateChatbotCommand command, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        var source = await chatbots.FindAsync(command.TenantId, command.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(command.ChatbotId);
        var by = await caller.RequireAsync(command.TenantId, cancellationToken);
        var name = await FreeCopyNameAsync(command.TenantId, source.Name, cancellationToken);

        var now = clock.UtcNow;
        var copy = Chatbot.Create(
            command.TenantId, name, source.Description, source.Trigger, source.Keywords, source.DraftFlowJson, source.DraftLayoutJson, by, now);
        copy.UpdateSettings(source.Trigger, source.Keywords, source.RestartKeyword, source.MaxAttempts, now);
        await chatbots.AddAsync(copy, cancellationToken);
        audit.Record(command.TenantId, executionContext.SubjectId, false, AutomationsAuditActions.ChatbotCreated, AutomationsAuditActions.ResourceChatbot, copy.Id, now);
        // Dos duplicados simultáneos pueden elegir el mismo nombre: el índice (IX_chatbots_tenant_name) responde 409 name_taken al segundo.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await builder.BuildAsync(copy, cancellationToken);
    }

    private async Task<string> FreeCopyNameAsync(Guid tenantId, string name, CancellationToken cancellationToken)
    {
        for (var number = 1; number <= ChatbotLimits.MaxCopyNumber; number++)
        {
            var suffix = number == 1 ? " (copia)" : $" (copia {number})";
            // El nombre base se recorta para que el sufijo quepa en los 80 (D-C12), en vez de un 422 name_invalid al duplicar.
            var baseName = name.Length + suffix.Length > Chatbot.NameMaxLength
                ? name[..(Chatbot.NameMaxLength - suffix.Length)].TrimEnd()
                : name;
            var candidate = baseName + suffix;
            if (!await chatbots.NameExistsAsync(tenantId, candidate, null, cancellationToken))
            {
                return candidate;
            }
        }

        throw new ResourceConflictException(
            AutomationsErrorCodes.ChatbotNameTaken, "Ya hay demasiadas copias de este chatbot: ponle otro nombre a alguna.");
    }
}

/// <summary>§4.4 y D-C13: borrar libera las conexiones; versiones y conexiones se van por la cascada de la base. Con corridas vivas
/// (<c>Running</c> o <c>Waiting</c>) es 409 <c>has_live_runs</c>: cortarlas dejaría al cliente hablando solo (D-C5). La auditoría se
/// registra antes de quitar el chatbot para que el mismo <c>SaveChangesAsync</c> confirme las dos cosas.</summary>
public sealed class DeleteChatbotHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IChatbotRepository chatbots,
    IConversationRunQueries runs,
    IAutomationsUnitOfWork unitOfWork,
    IAutomationsAuditRecorder audit,
    IClock clock)
    : ICommandHandler<DeleteChatbotCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteChatbotCommand command, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        var chatbot = await chatbots.FindAsync(command.TenantId, command.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(command.ChatbotId);
        AutomationsVersions.Ensure(chatbot.Version, command.Version);
        if (await runs.HasLiveRunsAsync(command.TenantId, chatbot.Id, cancellationToken))
        {
            throw new ResourceConflictException(
                AutomationsErrorCodes.ChatbotHasLiveRuns, "El chatbot tiene conversaciones en curso: pausa el chatbot y espera a que terminen.");
        }

        audit.Record(command.TenantId, executionContext.SubjectId, false, AutomationsAuditActions.ChatbotDeleted, AutomationsAuditActions.ResourceChatbot, chatbot.Id, clock.UtcNow);
        chatbots.Remove(chatbot);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>D-C9: el borrador del chatbot con sus ajustes, sin conexiones ni estado, más las «API externa» que nombran sus bloques HTTP
/// (resueltas por el puerto: el archivo dice cuáles hay que tener, no sólo sus ids). Leer el borrador es lo mismo que
/// <c>GET /chatbots/{id}</c>, pero es un archivo que sale del sistema: exige <c>manage</c> (§4.4).</summary>
public sealed class ExportChatbotHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IChatbotRepository chatbots,
    IAutomationExternalApis externalApis,
    IClock clock)
    : IQueryHandler<ExportChatbotQuery, ChatbotExportDto>
{
    public async Task<ChatbotExportDto> HandleAsync(ExportChatbotQuery query, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        var chatbot = await chatbots.FindAsync(query.TenantId, query.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(query.ChatbotId);

        var referenced = ReferencedApiIds(chatbot.DraftFlowJson);
        var known = (await externalApis.ListAsync(query.TenantId, cancellationToken)).ToDictionary(api => api.Id);
        return new ChatbotExportDto(
            ChatbotExportDto.CurrentSchema,
            chatbot.Name,
            chatbot.Description,
            chatbot.Trigger.ToString(),
            chatbot.Keywords,
            chatbot.RestartKeyword,
            chatbot.MaxAttempts,
            JsonSerializer.Deserialize<JsonElement>(chatbot.DraftFlowJson),
            JsonSerializer.Deserialize<JsonElement>(chatbot.DraftLayoutJson),
            [.. referenced.Select(id => known.TryGetValue(id, out var api)
                ? new ExternalApiRefDto(api.Id, api.Name, api.Status)
                : new ExternalApiRefDto(id, string.Empty, "Unknown"))],
            clock.UtcNow);
    }

    /// <summary>Un borrador ilegible (dato viejo) se exporta igual, sin APIs: el archivo es el respaldo, no la validación.</summary>
    private static IReadOnlyList<Guid> ReferencedApiIds(string flowJson)
    {
        try
        {
            return [.. FlowJson.Parse(flowJson).Nodes.OfType<HttpNode>().Select(node => node.ConnectionId).OfType<Guid>().Distinct().Order()];
        }
        catch (FlowFormatException)
        {
            return [];
        }
    }
}

/// <summary>D-C9 y D-C15: crea un chatbot en <c>Draft</c> sin conexiones con el flujo y los ajustes del archivo. El mapa reescribe el
/// <c>connectionId</c> de los bloques HTTP; el que no tiene par se queda, y Validar lo marca <c>http.connection_missing</c> en vez de
/// que importar falle. Un flujo ilegible es 422 bajo <c>export.flow</c>; el nombre repetido, 409 <c>name_taken</c> (el índice
/// respalda al chequeo).</summary>
public sealed class ImportChatbotHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    AutomationsCaller caller,
    IChatbotRepository chatbots,
    IAutomationsUnitOfWork unitOfWork,
    IAutomationsAuditRecorder audit,
    IClock clock,
    IValidator<ImportChatbotCommand> validator,
    ChatbotEditorBuilder builder)
    : ICommandHandler<ImportChatbotCommand, ChatbotEditorDto>
{
    public async Task<ChatbotEditorDto> HandleAsync(ImportChatbotCommand command, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var export = command.Request.Export;
        var flow = Remap(Parse(export.Flow), command.Request.ConnectionMap);
        var by = await caller.RequireAsync(command.TenantId, cancellationToken);
        var name = command.Request.Name ?? export.Name;
        await ChatbotConnectionCheck.EnsureNameIsFreeAsync(chatbots, command.TenantId, name, null, cancellationToken);

        var now = clock.UtcNow;
        // El validador ya garantizó el disparador: esta conversión no lanza.
        var trigger = Enum.Parse<ChatbotTrigger>(export.Trigger);
        var chatbot = Chatbot.Create(
            command.TenantId, name, export.Description, trigger, export.Keywords, FlowJson.Serialize(flow), export.Layout.GetRawText(), by, now);
        chatbot.UpdateSettings(trigger, export.Keywords, export.RestartKeyword, export.MaxAttempts, now);
        await chatbots.AddAsync(chatbot, cancellationToken);
        audit.Record(command.TenantId, executionContext.SubjectId, false, AutomationsAuditActions.ChatbotImported, AutomationsAuditActions.ResourceChatbot, chatbot.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await builder.BuildAsync(chatbot, cancellationToken);
    }

    private static FlowDefinition Parse(JsonElement flow)
    {
        try
        {
            return FlowJson.Parse(flow.GetRawText());
        }
        catch (FlowFormatException exception)
        {
            throw FlowFormatFailures.For("export.flow", exception);
        }
    }

    private static FlowDefinition Remap(FlowDefinition flow, IReadOnlyDictionary<Guid, Guid>? map)
    {
        if (map is null or { Count: 0 })
        {
            return flow;
        }

        return flow with
        {
            Nodes = [.. flow.Nodes.Select(node => node is HttpNode { ConnectionId: { } from } http && map.TryGetValue(from, out var to)
                ? http with { ConnectionId = to }
                : node)],
        };
    }
}
