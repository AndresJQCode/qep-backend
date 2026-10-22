using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Flow;
using Modules.Tenancy.Application;

namespace Modules.Automations.Application;

// Spec 2026-10-21 §4.4: lista, creación (en blanco; plantilla y duplicado llegan con la tarea 17 del plan), lectura, edición, borrador,
// validación, publicación, pausa, reanudación, duplicado y borrado del chatbot.

public sealed record ListChatbotsQuery(Guid TenantId, string? Search) : IQuery<ChatbotListResponse>;

public sealed record GetChatbotQuery(Guid TenantId, Guid ChatbotId) : IQuery<ChatbotEditorDto>;

/// <summary><see cref="Kind"/> es <c>blank</c>, <c>template</c> o <c>duplicate</c> (§4.4).</summary>
public sealed record ChatbotSource(string Kind, string? TemplateId, Guid? ChatbotId, IReadOnlyDictionary<string, string>? Parameters)
{
    public const string Blank = "blank";
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

/// <summary>Los límites del cuerpo (§4.4) y de los campos que comparten crear y editar.</summary>
public static class ChatbotLimits
{
    /// <summary>§4.4: el borrador pesa a lo sumo 512 KB serializado, en bytes UTF-8 (lo que viaja y lo que guarda el jsonb).</summary>
    public const int FlowMaxLength = 512 * 1024;

    /// <summary>Las posiciones no se validan (D-C2), pero no pueden crecer sin tope. En bytes UTF-8.</summary>
    public const int LayoutMaxLength = 256 * 1024;

    /// <summary>El tope del cuerpo de <c>PUT /draft</c>, antes de leerlo: flujo, posiciones y margen para el sobre JSON.</summary>
    public const int DraftBodyMaxBytes = 800 * 1024;

    /// <summary>Brief de la tarea 14: cuántas conexiones puede atender un chatbot.</summary>
    public const int MaxConnections = 10;

    /// <summary>El flujo de un chatbot recién creado en blanco y sus posiciones (brief de la tarea 14).</summary>
    public const string BlankFlow = """{"schema":1,"start":{"firstNodeId":null},"variables":[],"nodes":[]}""";

    public const string BlankLayout = """{"positions":{}}""";

    /// <summary>D-C12: hasta «(copia 50)». Más allá es un nombre que el tenant tiene que elegir a mano (409 <c>name_taken</c>).</summary>
    public const int MaxCopyNumber = 50;
}

/// <summary>Las reglas que comparten crear y editar. Texto libre ⇒ validador aunque el agregado valide (CLAUDE.md): el validador da
/// el campo; y así <c>MaxAttempts</c> fuera de 1..5 nunca llega al <c>ArgumentOutOfRangeException</c> del agregado (500), ni una
/// descripción larga a su recorte silencioso.</summary>
internal static class ChatbotRules
{
    public static void Name<T>(IRuleBuilderInitial<T, string> rule) => rule.NotEmpty().MaximumLength(Chatbot.NameMaxLength);

    public static void Description<T>(IRuleBuilderInitial<T, string> rule) => rule.NotNull().MaximumLength(Chatbot.DescriptionMaxLength);

    public static void Trigger<T>(IRuleBuilderInitial<T, string> rule) =>
        rule.Must(trigger => TryParseTrigger(trigger, out _))
            .WithMessage("Trigger must be NewConversation, Keyword or OutsideHours.");

    public static void Keywords<T>(IRuleBuilderInitial<T, IReadOnlyList<string>> rule) =>
        rule.NotNull()
            .Must(keywords => keywords.Count <= Chatbot.MaxKeywords)
            .WithMessage($"At most {Chatbot.MaxKeywords} keywords are allowed.")
            .Must(keywords => keywords.All(keyword => keyword is not null && keyword.Trim().Length <= Chatbot.KeywordMaxLength))
            .WithMessage($"Each keyword can have at most {Chatbot.KeywordMaxLength} characters.");

    public static void ConnectionIds<T>(IRuleBuilderInitial<T, IReadOnlyList<Guid>> rule) =>
        rule.NotNull()
            .Must(ids => ids.Count <= ChatbotLimits.MaxConnections)
            .WithMessage($"At most {ChatbotLimits.MaxConnections} connections are allowed.")
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .WithMessage("A connection cannot be repeated.");

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
        // La tarea 17 del plan agrega template y duplicate.
        RuleFor(command => command.Source).NotNull();
        RuleFor(command => command.Source.Kind)
            .Equal(ChatbotSource.Blank)
            .When(command => command.Source is not null)
            .WithMessage("Source kind must be blank.");
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

/// <summary>Nace en borrador (D-C1) con sus conexiones inactivas: un borrador no atiende a nadie (D-C13).</summary>
public sealed class CreateChatbotHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    AutomationsCaller caller,
    IChatbotRepository chatbots,
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
        var tenantConnections = await connections.ListAsync(command.TenantId, cancellationToken);
        ChatbotConnectionCheck.Ensure(tenantConnections, command.ConnectionIds);
        await ChatbotConnectionCheck.EnsureNameIsFreeAsync(chatbots, command.TenantId, command.Name, null, cancellationToken);

        var now = clock.UtcNow;
        // El validador ya garantizó el disparador: esta conversión no lanza.
        var chatbot = Chatbot.Create(
            command.TenantId, command.Name, command.Description, Enum.Parse<ChatbotTrigger>(command.Trigger), command.Keywords, ChatbotLimits.BlankFlow, ChatbotLimits.BlankLayout, by, now);
        chatbot.SetConnections(command.ConnectionIds, now);
        await chatbots.AddAsync(chatbot, cancellationToken);
        audit.Record(command.TenantId, executionContext.SubjectId, false, AutomationsAuditActions.ChatbotCreated, AutomationsAuditActions.ResourceChatbot, chatbot.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await builder.BuildAsync(chatbot, tenantConnections, cancellationToken);
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
    IClock clock,
    IValidator<SaveChatbotDraftCommand> validator,
    ChatbotEditorBuilder builder)
    : ICommandHandler<SaveChatbotDraftCommand, DraftSavedDto>
{
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
        audit.Record(command.TenantId, executionContext.SubjectId, false, AutomationsAuditActions.ChatbotDraftSaved, AutomationsAuditActions.ResourceChatbot, chatbot.Id, now);
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
            throw new ValidationException([new ValidationFailure("flow", exception.Code)]);
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

        var issues = await builder.ValidateStoredAsync(chatbot, tenantConnections, cancellationToken);
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
