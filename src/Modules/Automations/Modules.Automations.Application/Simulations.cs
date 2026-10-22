using System.Text.Json;
using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Automations.Application.Engine;
using Modules.Automations.Application.Http;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;
using Modules.Tenancy.Application;

namespace Modules.Automations.Application;

// Spec 2026-10-21 §4.4, §8 y D-S1..D-S3: el simulador del editor. Corre el borrador con el mismo FlowEngine que atiende a los clientes y,
// en vez de mandar mensajes, escribe la transcripción. Nunca toca conversaciones, el inbox ni las corridas.

/// <summary>Contra qué corre una simulación. <see cref="ConnectionId"/> es la primera conexión del chatbot, la que pregunta el bloque Horario
/// (D-S3); <see cref="Guid.Empty"/> si no tiene ninguna, y entonces rige el horario predeterminado del tenant (Mensajería resuelve así una
/// conexión sin horario asignado, <c>BusinessHoursRepository.GetStatusesAsync</c>), o abierto si el tenant no tiene ninguno.</summary>
public sealed record SimulationContext(Guid TenantId, Guid SimulationId, Guid ConnectionId, bool LiveHttp);

/// <summary>
/// Spec 2026-10-21 §8: el <c>SimulatedChannel</c>. Mismo bucle que <see cref="RunEffects"/> —cada efecto en orden, y el resultado de un horario
/// o de un HTTP vuelve a entrar al motor en la misma llamada—, pero un envío es una línea <c>bot</c>, y traspasar, resolver y terminar sólo
/// escriben una línea <c>system</c> (D-S3: tocarían conversaciones reales). El bloque HTTP usa su respuesta de ejemplo salvo con
/// <see cref="SimulationContext.LiveHttp"/> (D-S2).
/// </summary>
public sealed class SimulationRunner(IHttpBlockExecutor http, IAutomationBusinessHours businessHours, IClock clock)
{
    /// <summary>D-S2: el <c>ErrorCode</c> con que sale por <c>error</c> un bloque sin respuesta de ejemplo.</summary>
    public const string NoSampleResponse = "http.no_sample_response";

    /// <summary>Entra al motor con <paramref name="input"/> y ejecuta lo que pida hasta que la corrida espere al cliente o termine. Devuelve el
    /// estado nuevo y sólo las líneas que agregó.</summary>
    public async Task<(RunState State, IReadOnlyList<TranscriptEntry> Transcript)> RunAsync(
        FlowDefinition flow, EngineSettings settings, RunState state, EngineInput input, SimulationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);
        var now = clock.UtcNow;
        var lines = new Lines(flow, now);
        var external = 0;
        EngineInput? next = input;
        while (next is not null)
        {
            var step = FlowEngine.Step(flow, settings, state, next, now);
            state = step.State;
            next = null;
            foreach (var effect in step.Effects)
            {
                switch (effect)
                {
                    case Effect.SendText text:
                        lines.Bot(text.NodeId, text.Text);
                        break;
                    case Effect.SendInteractive card:
                        lines.Bot(card.NodeId, card.Message);
                        break;
                    case Effect.WaitForCustomer:
                        // El estado ya dice en qué bloque espera: currentNodeId es lo que el lienzo marca.
                        break;
                    case Effect.CheckBusinessHours or Effect.CallHttp:
                        // P2-R40: el mismo tope de reentradas que el procesador.
                        if (++external > EngineReentries.Max)
                        {
                            lines.System(effect.NodeId, EndText(EndReasons.LoopDetected));
                            return (state with { Status = RunStatus.Failed }, lines.All);
                        }

                        next = effect is Effect.CallHttp call
                            ? await CallHttpAsync(call, state.Variables, context, lines, cancellationToken)
                            : new EngineInput.BusinessHours(
                                effect.NodeId, await businessHours.IsOpenAsync(context.TenantId, context.ConnectionId, cancellationToken));
                        break;
                    case Effect.Handoff handoff:
                        lines.System(handoff.NodeId, "Pasaría a un asesor");
                        break;
                    case Effect.Resolve resolve:
                        lines.System(resolve.NodeId, "Resolvería la conversación");
                        break;
                    case Effect.EndRun end:
                        lines.System(end.NodeId, EndText(end.Reason));
                        break;
                    default:
                        throw new InvalidOperationException($"Efecto del motor desconocido: {effect.GetType().Name}.");
                }
            }
        }

        return (state, lines.All);
    }

    /// <summary>
    /// D-S2. Sin la API real, el mapeo se aplica a la respuesta de ejemplo con el mismo paso del ejecutor (<see cref="HttpResponseMapping"/>) y
    /// sale por <c>ok</c> con 200. Con ella, el ejecutor llama y registra la fila de <c>http_calls</c> con la simulación y sin corrida. Un
    /// formulario con archivo no se manda: el adjunto del simulador es de mentira (no está en R2), y el ejecutor lo esperaría 20 s para
    /// terminar igual en <c>media_not_ready</c>.
    /// </summary>
    private async Task<EngineInput> CallHttpAsync(
        Effect.CallHttp call, RunVariables variables, SimulationContext context, Lines lines, CancellationToken cancellationToken)
    {
        var node = call.Node;
        if (!context.LiveHttp)
        {
            if (node.SampleResponse is not { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } sample)
            {
                lines.System(call.NodeId, "Este bloque no tiene respuesta de ejemplo");
                return new EngineInput.HttpResult(call.NodeId, false, null, RunVariables.Empty(), NoSampleResponse);
            }

            return new EngineInput.HttpResult(call.NodeId, true, 200, HttpResponseMapping.Apply(node.Mapping, JsonNode.Parse(sample.GetRawText())), null);
        }

        if (node.Body is { Kind: HttpBodyKinds.Multipart, FileField: not null, FileVariable: not null })
        {
            lines.System(call.NodeId, "El simulador no le envía archivos a la API: el bloque sigue por su salida de error");
            return new EngineInput.HttpResult(call.NodeId, false, null, RunVariables.Empty(), HttpCallOutcomes.MediaNotReady);
        }

        var result = await http.ExecuteAsync(new HttpCallContext(context.TenantId, RunId: null, context.SimulationId, call.NodeId), node, variables, cancellationToken);
        if (!result.Ok)
        {
            lines.System(call.NodeId, $"La llamada a la API falló ({result.ErrorCode ?? result.Outcome})");
        }

        return new EngineInput.HttpResult(call.NodeId, result.Ok, result.Status, result.Mapped, result.ErrorCode);
    }

    private static string EndText(string reason) => reason switch
    {
        EndReasons.OutputUnconnected => "Terminó: salida sin conectar",
        EndReasons.LoopDetected => "Terminó: el flujo da vueltas sin esperar al cliente",
        EndReasons.TargetMissing => "Terminó: una salida apunta a un bloque que no existe",
        _ => "Terminó",
    };

    /// <summary>Las líneas que agrega una llamada, con el título del bloque que las escribe (el primero con ese id, como el motor).</summary>
    private sealed class Lines(FlowDefinition flow, DateTimeOffset now)
    {
        private readonly List<TranscriptEntry> entries = [];

        public IReadOnlyList<TranscriptEntry> All => entries;

        public void Bot(string nodeId, string text) => Add(TranscriptSenders.Bot, nodeId, text);

        public void Bot(string nodeId, InteractiveMessage message)
        {
            switch (message)
            {
                case InteractiveMessage.Buttons buttons:
                    Add(TranscriptSenders.Bot, nodeId, buttons.Body, buttons: [.. buttons.Items.Select(item => new TranscriptButton(item.Id, item.Title))]);
                    break;
                case InteractiveMessage.List list:
                    // Las filas ya vienen ajustadas por el motor (P2-R15): son las que el cliente vería.
                    Add(TranscriptSenders.Bot, nodeId, list.Body, list: new TranscriptList(
                        list.ButtonLabel, [.. list.Rows.Select(row => new TranscriptRow(row.Id, row.Title, row.Description))]));
                    break;
                case InteractiveMessage.CtaUrl cta:
                    Add(TranscriptSenders.Bot, nodeId, cta.Body, urlButton: new TranscriptUrlButton(cta.Label, cta.Url));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(message), message.GetType().Name, "Interactivo desconocido.");
            }
        }

        public void System(string nodeId, string text) => Add(TranscriptSenders.System, nodeId, text);

        private void Add(
            string from, string nodeId, string text, IReadOnlyList<TranscriptButton>? buttons = null, TranscriptList? list = null,
            TranscriptUrlButton? urlButton = null) =>
            entries.Add(new TranscriptEntry(
                from, nodeId, flow.Nodes.FirstOrDefault(node => node.Id == nodeId)?.Title, text, buttons, list, urlButton, now));
    }
}

/// <summary>
/// Spec 2026-10-21 §4.4 y §8: lo que el panel Simular pinta después de cada paso. <see cref="Transcript"/> viaja entera (la pantalla la
/// repinta, no la mezcla con lo que tenía); <see cref="CurrentNodeId"/> es el bloque que el lienzo marca; <see cref="Status"/> es el nombre
/// del <c>RunStatus</c> y <see cref="Ended"/> le ahorra al panel conocer cuáles terminan: con <c>true</c> no hay a quién escribirle.
/// <see cref="Variables"/> son las de la corrida, como objeto JSON.
/// </summary>
public sealed record SimulationDto(
    Guid Id,
    Guid ChatbotId,
    bool LiveHttp,
    string? CurrentNodeId,
    string Status,
    bool Ended,
    JsonElement Variables,
    IReadOnlyList<TranscriptEntry> Transcript,
    DateTimeOffset ExpiresAt);

/// <summary>El adjunto de mentira del tester (§8): sólo nombre y tipo; no hay archivo.</summary>
public sealed record SimulationAttachmentRequest(string FileName, string MimeType);

public sealed record StartSimulationCommand(Guid TenantId, Guid ChatbotId, bool LiveHttp) : ICommand<SimulationDto>;

/// <summary>Texto escrito, el <paramref name="ReplyId"/> de un botón o una fila, o un adjunto ficticio (§8).</summary>
public sealed record SendSimulationMessageCommand(
    Guid TenantId, Guid SimulationId, string? Text, string? ReplyId, SimulationAttachmentRequest? Attachment) : ICommand<SimulationDto>;

public sealed record DeleteSimulationCommand(Guid TenantId, Guid SimulationId) : ICommand<bool>;

public static class SimulationLimits
{
    /// <summary>El tope de un texto de WhatsApp: el tester no escribe nada que un cliente no pudiera mandar.</summary>
    public const int TextMaxLength = 4096;

    /// <summary>El tope de un id de fila (InteractiveLimits de Mensajería); el de un botón es menor.</summary>
    public const int ReplyIdMaxLength = 200;

    public const int FileNameMaxLength = 255;

    public const int MimeTypeMaxLength = 127;
}

public sealed class SendSimulationMessageValidator : AbstractValidator<SendSimulationMessageCommand>
{
    public SendSimulationMessageValidator()
    {
        RuleFor(command => command.Text)
            .Must((command, text) => !string.IsNullOrWhiteSpace(text) || !string.IsNullOrEmpty(command.ReplyId) || command.Attachment is not null)
            .WithMessage("Send a text, a reply id or an attachment.")
            .MaximumLength(SimulationLimits.TextMaxLength);
        RuleFor(command => command.ReplyId).MaximumLength(SimulationLimits.ReplyIdMaxLength);
        When(command => command.Attachment is not null, () =>
        {
            RuleFor(command => command.Attachment!.FileName).NotEmpty().MaximumLength(SimulationLimits.FileNameMaxLength);
            RuleFor(command => command.Attachment!.MimeType)
                .NotEmpty()
                .MaximumLength(SimulationLimits.MimeTypeMaxLength)
                .Must(mimeType => mimeType.Contains('/', StringComparison.Ordinal))
                .WithMessage("The MIME type must look like type/subtype.");
        });
    }
}

/// <summary>Lo que comparten los tres casos de uso: el chatbot de la simulación, su conexión y el DTO.</summary>
internal static class SimulationSupport
{
    /// <summary>D-M8 en el simulador (§8): las variables fijas no son de un cliente real.</summary>
    public const string CustomerName = "Cliente de prueba";

    public const string Phone = "3000000000";

    /// <summary><c>{conexion}</c> de un chatbot que todavía no tiene conexiones.</summary>
    public const string NoConnectionName = "Simulador";

    /// <summary>La primera conexión del chatbot que el tenant todavía tiene, en el orden de la lista de conexiones (una que Integrations ya
    /// borró no cuenta, como en el editor).</summary>
    public static AutomationConnectionRef? FirstConnection(Chatbot chatbot, IReadOnlyList<AutomationConnectionRef> tenantConnections)
    {
        var own = chatbot.Connections.Select(connection => connection.ConnectionId).ToHashSet();
        return tenantConnections.FirstOrDefault(connection => own.Contains(connection.Id));
    }

    public static SimulationContext ContextOf(Simulation simulation, AutomationConnectionRef? connection) =>
        new(simulation.TenantId, simulation.Id, connection?.Id ?? Guid.Empty, simulation.LiveHttp);

    public static EngineSettings SettingsOf(Chatbot chatbot) => new(chatbot.RestartKeyword, chatbot.MaxAttempts);

    public static SimulationDto ToDto(Simulation simulation)
    {
        var state = simulation.ToState();
        return new SimulationDto(
            simulation.Id, simulation.ChatbotId, simulation.LiveHttp, state.CurrentNodeId, state.Status.ToString(), simulation.IsEnded,
            JsonSerializer.Deserialize<JsonElement>(state.Variables.ToJson()), simulation.ReadTranscript(), simulation.ExpiresAt);
    }
}

/// <summary>
/// §4.4 y D-S1: arranca sobre el borrador (no sobre la versión publicada), validado con el contexto real como al publicar: con errores es 422
/// <c>automations.chatbot.invalid</c> con los errores bajo <c>errors.flow</c>. Responde la transcripción inicial: lo que el bot dice hasta la
/// primera espera.
/// </summary>
public sealed class StartSimulationHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    AutomationsCaller caller,
    IChatbotRepository chatbots,
    IAutomationConnections connections,
    IAutomationTenantNames tenantNames,
    ISimulationRepository simulations,
    IAutomationsUnitOfWork unitOfWork,
    SimulationRunner runner,
    IClock clock,
    ChatbotEditorBuilder builder)
    : ICommandHandler<StartSimulationCommand, SimulationDto>
{
    public async Task<SimulationDto> HandleAsync(StartSimulationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        var chatbot = await chatbots.FindAsync(command.TenantId, command.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(command.ChatbotId);
        var memberId = await caller.RequireAsync(command.TenantId, cancellationToken);
        var tenantConnections = await connections.ListAsync(command.TenantId, cancellationToken);

        ChatbotFlowErrors.ThrowIfAny(
            await builder.ValidateStoredAsync(chatbot, tenantConnections, cancellationToken), "El borrador tiene errores: corrígelos para simularlo.");
        // Validar ya lo leyó: un borrador que no se puede leer es un error del validador y no llega hasta acá.
        var flow = FlowJson.Parse(chatbot.DraftFlowJson);

        var connection = SimulationSupport.FirstConnection(chatbot, tenantConnections);
        var initial = RunState.Initial(FixedVariables.Build(
            SimulationSupport.CustomerName, SimulationSupport.Phone, await tenantNames.FindAsync(command.TenantId, cancellationToken),
            connection?.Name ?? SimulationSupport.NoConnectionName));
        var simulation = Simulation.Start(command.TenantId, chatbot.Id, memberId, command.LiveHttp, initial, clock.UtcNow);

        var (state, transcript) = await runner.RunAsync(
            flow, SimulationSupport.SettingsOf(chatbot), initial, new EngineInput.Start(), SimulationSupport.ContextOf(simulation, connection), cancellationToken);
        simulation.Apply(state, transcript, clock.UtcNow);
        await simulations.AddAsync(simulation, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return SimulationSupport.ToDto(simulation);
    }
}

/// <summary>
/// §4.4 y §8: el mensaje del tester entra como <see cref="EngineInput.CustomerMessage"/> sobre el borrador actual (si el admin lo cambió,
/// sigue con el cambio; un bloque que ya no existe termina la simulación como en una corrida, P2-R17). Vencida es 404
/// <c>automations.simulation.expired</c> hasta que el barrido la borra. La de otro miembro del tenant también se puede seguir: la ve quien
/// tenga <c>manage</c>. Una que ya terminó no espera nada y vuelve tal cual, sin anotar el mensaje.
/// </summary>
public sealed class SendSimulationMessageHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IChatbotRepository chatbots,
    IAutomationConnections connections,
    ISimulationRepository simulations,
    IAutomationsUnitOfWork unitOfWork,
    SimulationRunner runner,
    IClock clock,
    IValidator<SendSimulationMessageCommand> validator)
    : ICommandHandler<SendSimulationMessageCommand, SimulationDto>
{
    public async Task<SimulationDto> HandleAsync(SendSimulationMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var simulation = await simulations.FindAsync(command.TenantId, command.SimulationId, cancellationToken)
            ?? throw AutomationsNotFound.Simulation(command.SimulationId);
        var now = clock.UtcNow;
        if (simulation.IsExpired(now))
        {
            throw AutomationsNotFound.SimulationExpired(simulation.Id);
        }

        if (simulation.IsEnded)
        {
            return SimulationSupport.ToDto(simulation);
        }

        var chatbot = await chatbots.FindAsync(command.TenantId, simulation.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(simulation.ChatbotId);
        var flow = ParseDraft(chatbot);
        var connection = SimulationSupport.FirstConnection(chatbot, await connections.ListAsync(command.TenantId, cancellationToken));
        var transcript = simulation.ReadTranscript();
        var customer = new TranscriptEntry(TranscriptSenders.Customer, null, null, CustomerText(command, transcript), null, null, null, now);

        var (state, appended) = await runner.RunAsync(
            flow, SimulationSupport.SettingsOf(chatbot), simulation.ToState(), CustomerMessage(command),
            SimulationSupport.ContextOf(simulation, connection), cancellationToken);
        simulation.Apply(state, [customer, .. appended], clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return SimulationSupport.ToDto(simulation);
    }

    /// <summary>§8: el adjunto es de mentira (sin mensaje ni archivo en R2, <c>Stored: false</c>); su tipo sale del MIME, como en un entrante.</summary>
    private static EngineInput.CustomerMessage CustomerMessage(SendSimulationMessageCommand command)
    {
        if (command.Attachment is not { } attachment)
        {
            return new EngineInput.CustomerMessage(CustomerMessageKinds.Text, command.Text, command.ReplyId, null);
        }

        var kind = attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? CustomerMessageKinds.Image : CustomerMessageKinds.Document;
        return new EngineInput.CustomerMessage(
            kind, command.Text, command.ReplyId, new AttachmentInput(Guid.Empty, attachment.FileName, attachment.MimeType, Stored: false));
    }

    /// <summary>Lo que la línea del tester muestra: lo que escribió; si tocó una opción, su título (lo que vería en WhatsApp), buscado en el
    /// último mensaje del bot que la ofrecía; si mandó un adjunto, su nombre.</summary>
    private static string CustomerText(SendSimulationMessageCommand command, IReadOnlyList<TranscriptEntry> transcript)
    {
        if (!string.IsNullOrWhiteSpace(command.Text))
        {
            return command.Text;
        }

        if (command.ReplyId is { } replyId)
        {
            var offered = transcript.LastOrDefault(entry => entry.From == TranscriptSenders.Bot && (entry.Buttons is not null || entry.List is not null));
            return offered?.Buttons?.FirstOrDefault(button => button.Id == replyId)?.Title
                ?? offered?.List?.Rows.FirstOrDefault(row => row.Id == replyId)?.Title
                ?? replyId;
        }

        return command.Attachment?.FileName ?? string.Empty;
    }

    /// <summary>El borrador pudo cambiar después del arranque; uno que no se puede leer es el mismo 422 que daría arrancar.</summary>
    private static FlowDefinition ParseDraft(Chatbot chatbot)
    {
        try
        {
            return FlowJson.Parse(chatbot.DraftFlowJson);
        }
        catch (FlowFormatException exception)
        {
            ChatbotFlowErrors.ThrowIfAny(
                [new FlowIssueDto(nameof(FlowIssueSeverity.Error), exception.Code, null, exception.Message)],
                "El borrador tiene errores: corrígelos para simularlo.");
            throw;
        }
    }
}

/// <summary>§4.4: cerrar el panel borra la simulación; también una vencida que el barrido todavía no alcanzó.</summary>
public sealed class DeleteSimulationHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, ISimulationRepository simulations, IAutomationsUnitOfWork unitOfWork)
    : ICommandHandler<DeleteSimulationCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteSimulationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        var simulation = await simulations.FindAsync(command.TenantId, command.SimulationId, cancellationToken)
            ?? throw AutomationsNotFound.Simulation(command.SimulationId);
        simulations.Remove(simulation);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
