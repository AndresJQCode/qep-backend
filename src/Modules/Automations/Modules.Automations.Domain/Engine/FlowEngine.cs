using System.Globalization;
using System.Text.Json.Nodes;
using Modules.Automations.Domain.Flow;
using Modules.Automations.Domain.Text;

namespace Modules.Automations.Domain.Engine;

/// <summary>
/// Spec 2026-10-21 D-M2 y §5.4: un paso del motor es una función pura. Recibe el flujo, el estado de la corrida y lo que pasó, y devuelve
/// el estado nuevo y los efectos que el procesador (o el simulador) ejecuta en orden. Avanza de bloque en bloque hasta uno que necesite al
/// mundo (espera del cliente, Llamada HTTP, Horario de atención) o hasta un final. No lee reloj, base ni red: <c>now</c> llega de afuera.
/// </summary>
public static class FlowEngine
{
    /// <summary>El <c>NodeId</c> del <see cref="Effect.EndRun"/> cuando el Inicio no está conectado (el validador no lo deja publicar, pero el
    /// simulador corre borradores).</summary>
    public const string StartNodeId = "start";

    public static StepResult Step(FlowDefinition flow, EngineSettings settings, RunState state, EngineInput input, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(input);

        return input switch
        {
            EngineInput.Start => new Walk(flow, state, now).Go(flow.Start.FirstNodeId, StartNodeId),
            EngineInput.HttpResult result => OnHttpResult(flow, state, result, now),
            EngineInput.BusinessHours hours => OnBusinessHours(flow, state, hours, now),
            EngineInput.CustomerMessage message => OnCustomerMessage(flow, settings, state, message, now),
            _ => throw new ArgumentOutOfRangeException(nameof(input), input.GetType().Name, "Entrada del motor desconocida."),
        };
    }

    /// <summary>
    /// La respuesta del cliente al bloque en que la corrida espera (§5.4, D-I5, D-M7). La palabra de reinicio gana sobre cualquier
    /// clasificación: escrita en una pregunta abierta sin validación no se guarda como respuesta. Un mensaje con la corrida no
    /// <c>Waiting</c> (el procesador no debería mandarlo) no cambia nada.
    /// </summary>
    private static StepResult OnCustomerMessage(
        FlowDefinition flow, EngineSettings settings, RunState state, EngineInput.CustomerMessage message, DateTimeOffset now)
    {
        if (WaitingAt(flow, state) is not { } node)
        {
            return Unchanged(state);
        }

        var walk = new Walk(flow, state, now);
        if (IsRestart(settings, message))
        {
            return walk.Restart(node);
        }

        if (CustomerReply.Classify(node, message, state.Variables) is ReplyOutcome.Output output)
        {
            walk.Save(SaveToOf(node), output.Saved);
            return walk.Leave(node, output.Name);
        }

        return walk.Reject(node, settings);
    }

    /// <summary>D-I5: la palabra se compara como las palabras clave (sin tildes, mayúsculas ni espacios de más). Sin palabra configurada
    /// no reinicia nada: un texto vacío no puede reiniciar la corrida.</summary>
    private static bool IsRestart(EngineSettings settings, EngineInput.CustomerMessage message) =>
        !string.IsNullOrWhiteSpace(settings.RestartKeyword) && !string.IsNullOrWhiteSpace(message.Text)
        && TextMatch.Same(message.Text, settings.RestartKeyword);

    private static FlowNode? WaitingAt(FlowDefinition flow, RunState state) =>
        state.Status == RunStatus.Waiting && state.CurrentNodeId is { } nodeId
            ? flow.Nodes.FirstOrDefault(node => node.Id == nodeId && Waits(node))
            : null;

    private static bool Waits(FlowNode node) => node is OpenQuestionNode or ButtonsNode or ListNode or AttachmentNode;

    private static string? SaveToOf(FlowNode node) => node switch
    {
        OpenQuestionNode question => question.SaveTo,
        ButtonsNode buttons => buttons.SaveTo,
        ListNode list => list.SaveTo,
        AttachmentNode attachment => attachment.SaveTo,
        _ => null,
    };

    private static StepResult OnHttpResult(FlowDefinition flow, RunState state, EngineInput.HttpResult result, DateTimeOffset now)
    {
        if (PendingAt<HttpNode>(flow, state, result.NodeId) is not { } node)
        {
            return Unchanged(state);
        }

        var walk = new Walk(flow, state, now);
        if (result.Ok)
        {
            walk.Merge(result.Mapped);
        }

        return walk.Leave(node, result.Ok ? FlowOutputs.Ok : FlowOutputs.Error);
    }

    private static StepResult OnBusinessHours(FlowDefinition flow, RunState state, EngineInput.BusinessHours hours, DateTimeOffset now) =>
        PendingAt<BusinessHoursNode>(flow, state, hours.NodeId) is { } node
            ? new Walk(flow, state, now).Leave(node, hours.IsOpen ? FlowOutputs.Open : FlowOutputs.Closed)
            : Unchanged(state);

    /// <summary>
    /// El bloque que pidió el efecto externo, si la corrida sigue parada en él. Un resultado de otro bloque (la fila se reintentó y la
    /// corrida ya avanzó, o el procesador reejecutó un efecto viejo) no cambia nada.
    /// </summary>
    private static T? PendingAt<T>(FlowDefinition flow, RunState state, string nodeId)
        where T : FlowNode =>
        state.Status == RunStatus.Running && state.CurrentNodeId == nodeId
            ? flow.Nodes.OfType<T>().FirstOrDefault(node => node.Id == nodeId)
            : null;

    private static StepResult Unchanged(RunState state) => new(state, []);

    /// <summary>Un recorrido del flujo dentro de un solo paso: acumula efectos, trace y el estado nuevo sobre una copia de las variables.</summary>
    private sealed class Walk
    {
        private readonly Dictionary<string, FlowNode> nodes = new(StringComparer.Ordinal);
        private readonly HashSet<string> entered = new(StringComparer.Ordinal);
        private readonly List<Effect> effects = [];
        private readonly List<TraceEntry> trace;
        private readonly string? firstNodeId;
        private readonly DateTimeOffset now;
        private RunVariables variables;
        private RunStatus status = RunStatus.Running;
        private string? currentNodeId;
        private int stepNumber;
        private int attempts;

        public Walk(FlowDefinition flow, RunState state, DateTimeOffset now)
        {
            foreach (var node in flow.Nodes)
            {
                nodes.TryAdd(node.Id, node);
            }

            firstNodeId = flow.Start.FirstNodeId;
            trace = [.. state.Trace];
            variables = state.Variables.Clone();
            currentNodeId = state.CurrentNodeId;
            stepNumber = state.StepNumber;
            attempts = state.Attempts;
            this.now = now;
        }

        public void Merge(RunVariables mapped)
        {
            foreach (var (name, value) in mapped.Snapshot())
            {
                variables.Set(name, value);
            }
        }

        /// <summary>Guarda la respuesta del cliente en el <c>saveTo</c> del bloque; un Botones sin <c>saveTo</c> no guarda nada.</summary>
        public void Save(string? name, JsonNode? value)
        {
            if (!string.IsNullOrWhiteSpace(name) && value is not null)
            {
                variables.Set(name, value);
            }
        }

        /// <summary>
        /// Una respuesta que no es lo que <paramref name="node"/> espera (D-M7) es un intento. El <c>fallback</c> de Botones y Lista decide
        /// primero (ruling P2-R9); Pregunta abierta y Pedir adjunto siempre se comportan como <c>repeat</c>. Mientras queden intentos se
        /// reenvía el mensaje; al agotarlos se sale por <c>invalid</c>/<c>other</c> si está conectada, y si no, la corrida reinicia.
        /// </summary>
        public StepResult Reject(FlowNode node, EngineSettings settings)
        {
            attempts++;
            var fallback = node switch
            {
                ButtonsNode buttons => buttons.Fallback,
                ListNode list => list.Fallback,
                _ => InteractiveFallbacks.Repeat,
            };

            if (fallback == InteractiveFallbacks.Handoff)
            {
                effects.Add(new Effect.Handoff(node.Id));
                _ = Stop(node, RunStatus.HandedOff, TraceOutcomes.HandedOff);
                return Result();
            }

            var exhausted = node is OpenQuestionNode or AttachmentNode ? FlowOutputs.Invalid : FlowOutputs.Other;
            var connected = node.Outputs.GetValueOrDefault(exhausted) is not null;
            if (fallback == InteractiveFallbacks.OtherOutput && connected)
            {
                return Leave(node, FlowOutputs.Other);
            }

            // D-C7: un bloque de pregunta puede pisar los intentos del chatbot; los demás usan los del chatbot.
            var maxAttempts = node is OpenQuestionNode { MaxAttempts: { } own } ? own : settings.MaxAttempts;
            if (attempts < maxAttempts)
            {
                return Resend(node);
            }

            return connected ? Leave(node, exhausted) : Restart(node);
        }

        /// <summary>
        /// D-I5 y D-M7: la corrida vuelve a empezar con sólo las variables fijas y cero intentos, y se recorre desde el Inicio como un
        /// <see cref="EngineInput.Start"/>. Es un recorrido nuevo: volver a entrar en el primer bloque (que puede ser el que esperaba) no es
        /// un ciclo. <see cref="RunState.StepNumber"/> no vuelve a cero: sigue subiendo para que los clientIds no se repitan (ruling P2-R5).
        /// </summary>
        public StepResult Restart(FlowNode node)
        {
            variables = variables.FixedOnly();
            attempts = 0;
            Record(node, TraceOutcomes.Restart);
            entered.Clear();
            return Go(firstNodeId, StartNodeId);
        }

        /// <summary>Reenvía el mensaje del bloque que espera entrando otra vez por <see cref="Enter"/> (suma StepNumber, ruling P2-R5) y sin
        /// pasar por <see cref="Go"/>, que pondría los intentos en cero. Una Lista que al reentrar sale sola (<c>tooMany</c>) sigue desde ahí.</summary>
        private StepResult Resend(FlowNode node) =>
            Enter(node) is { } output ? Go(node.Outputs.GetValueOrDefault(output), node.Id) : Result();

        /// <summary>Sale de <paramref name="node"/> (donde la corrida estaba parada) por <paramref name="output"/> y sigue el recorrido.</summary>
        public StepResult Leave(FlowNode node, string output)
        {
            Record(node, output);
            return Go(node.Outputs.GetValueOrDefault(output), node.Id);
        }

        /// <summary>Entra en <paramref name="targetId"/> y en los que siguen hasta un bloque que espera o un final.</summary>
        public StepResult Go(string? targetId, string fromNodeId)
        {
            while (true)
            {
                if (targetId is null)
                {
                    effects.Add(new Effect.EndRun(fromNodeId, EndReasons.OutputUnconnected));
                    status = RunStatus.Completed;
                    return Result();
                }

                // Ruling P2-R14: sólo una versión guardada corrupta apunta a un bloque que no existe (el validador no publica
                // node.unknown_target). Igual que el tope de vueltas, se termina Failed en vez de lanzar, para que la conversación no
                // quede Active con un bot mudo; el NodeId es el bloque (o el Inicio) cuya salida apunta al vacío.
                if (!nodes.TryGetValue(targetId, out var node))
                {
                    effects.Add(new Effect.EndRun(fromNodeId, EndReasons.TargetMissing));
                    status = RunStatus.Failed;
                    return Result();
                }

                // Ruling P2-R13. Dentro de un recorrido nada cambia las variables (sólo una espera o un efecto externo lo hacen, y esos
                // cortan el recorrido), así que volver a entrar en un bloque ya es el ciclo: repetiría lo mismo para siempre. Se corta en
                // la primera vuelta, antes de reenviar nada; el bloque en que la corrida estaba parada (el de Leave) no cuenta como
                // entrado. El validador no rechaza esos ciclos al publicar. Lanzar no sirve: el procesador revertiría la unidad, la fila
                // se reintentaría cinco veces y la conversación quedaría Active con un bot mudo; terminar Failed deja que alguien la vea.
                if (!entered.Add(node.Id))
                {
                    effects.Add(new Effect.EndRun(node.Id, EndReasons.LoopDetected));
                    status = RunStatus.Failed;
                    return Result();
                }

                // Los intentos son del bloque que espera: un bloque nuevo empieza de cero (D-M7).
                attempts = 0;
                if (Enter(node) is not { } output)
                {
                    return Result();
                }

                targetId = node.Outputs.GetValueOrDefault(output);
                fromNodeId = node.Id;
            }
        }

        /// <summary>
        /// Entra en un bloque: lo anota, emite lo que §5.4 dice «al entrar» y devuelve la salida que toma, o <c>null</c> si la corrida se
        /// queda en él (espera, efecto externo o final). Es el único lugar que envía el mensaje de un bloque que espera, así que reenviarlo
        /// tras una respuesta inválida (tarea 3) es volver a entrar: también suma <see cref="RunState.StepNumber"/> (ruling P2-R5).
        /// </summary>
        private string? Enter(FlowNode node)
        {
            stepNumber++;
            currentNodeId = node.Id;
            switch (node)
            {
                case MessageNode message:
                    Send(message);
                    return Took(node, FlowOutputs.Next);
                case ConditionNode condition:
                    return Took(node, Holds(condition) ? FlowOutputs.Yes : FlowOutputs.No);
                case BusinessHoursNode:
                    effects.Add(new Effect.CheckBusinessHours(node.Id));
                    return Stop(node, RunStatus.Running, TraceOutcomes.Pending);
                case HttpNode http:
                    effects.Add(new Effect.CallHttp(node.Id, http));
                    return Stop(node, RunStatus.Running, TraceOutcomes.Pending);
                case HandoffNode handoff:
                    SendOptional(node.Id, handoff.Text);
                    effects.Add(new Effect.Handoff(node.Id));
                    return Stop(node, RunStatus.HandedOff, TraceOutcomes.HandedOff);
                case ResolveNode resolve:
                    SendOptional(node.Id, resolve.Text);
                    effects.Add(new Effect.Resolve(node.Id, resolve.ReasonId));
                    return Stop(node, RunStatus.Resolved, TraceOutcomes.Resolved);
                case ListNode list:
                    // §5.4: más filas de las que Meta acepta, o ninguna que mostrar, sale por tooMany sin enviar nada.
                    var rows = CustomerReply.RowsOf(list, variables);
                    if (rows.Count is 0 or > CustomerReply.MaxListRows)
                    {
                        return Took(node, FlowOutputs.TooMany);
                    }

                    effects.Add(new Effect.SendInteractive(node.Id, new InteractiveMessage.List(
                        Text(list.Text), list.ButtonLabel, [.. rows.Select(row => (row.Id, row.Title, row.Description))])));
                    return Wait(node);
                case OpenQuestionNode or ButtonsNode or AttachmentNode:
                    effects.Add(Prompt(node));
                    return Wait(node);
                default:
                    throw new ArgumentOutOfRangeException(nameof(node), node.Kind, "Bloque sin semántica en el motor.");
            }
        }

        private string? Wait(FlowNode node)
        {
            effects.Add(new Effect.WaitForCustomer(node.Id, node.Title));
            return Stop(node, RunStatus.Waiting, TraceOutcomes.Waiting);
        }

        private string Took(FlowNode node, string output)
        {
            Record(node, output);
            return output;
        }

        private string? Stop(FlowNode node, RunStatus stopped, string outcome)
        {
            Record(node, outcome);
            status = stopped;
            return null;
        }

        private void Record(FlowNode node, string outcome)
        {
            trace.Add(new TraceEntry(now, node.Id, node.Kind, outcome));

            // while y no if: un trace guardado con más de 200 entradas (de antes del tope) también se recorta.
            while (trace.Count > RunState.TraceMax)
            {
                trace.RemoveAt(0);
            }
        }

        private StepResult Result() =>
            new(new RunState(status, currentNodeId, stepNumber, attempts, variables, [.. trace]), [.. effects]);

        private string Text(string template) => TemplateText.Interpolate(template, variables);

        /// <summary>D-I1: con botón de URL sale como <c>CtaUrl</c>, que es la única forma de mandar un enlace como botón. Sin botón, un
        /// texto que queda en blanco al interpolar no se manda (como en Pasar a un asesor y Resolver), pero el bloque sigue por <c>next</c>.</summary>
        private void Send(MessageNode message)
        {
            if (message.UrlButton is { } button)
            {
                effects.Add(new Effect.SendInteractive(
                    message.Id, new InteractiveMessage.CtaUrl(Text(message.Text), Text(button.Label), Text(button.Url))));
            }
            else
            {
                SendOptional(message.Id, message.Text);
            }
        }

        private void SendOptional(string nodeId, string? template)
        {
            if (template is null)
            {
                return;
            }

            // Un texto que queda en blanco al interpolar no se manda: Meta rechaza un mensaje vacío y la corrida terminaría en error.
            var text = Text(template);
            if (!string.IsNullOrWhiteSpace(text))
            {
                effects.Add(new Effect.SendText(nodeId, text));
            }
        }

        /// <summary>
        /// El mensaje de un bloque que espera (la Lista lo arma <see cref="Enter"/>, que primero cuenta sus filas). Las etiquetas de los
        /// botones no se interpolan: <see cref="CustomerReply"/> compara contra ellas lo que el cliente escribe.
        /// </summary>
        private Effect Prompt(FlowNode node) => node switch
        {
            OpenQuestionNode question => new Effect.SendText(node.Id, Text(question.Text)),
            AttachmentNode attachment => new Effect.SendText(node.Id, Text(attachment.Text)),
            ButtonsNode buttons => new Effect.SendInteractive(node.Id, new InteractiveMessage.Buttons(
                Text(buttons.Text), [.. buttons.Buttons.Select(button => (button.Id, button.Label))])),
            _ => throw new ArgumentOutOfRangeException(nameof(node), node.Kind, "El bloque no espera al cliente."),
        };

        private bool Holds(ConditionNode condition) => condition.Match == "any"
            ? condition.Rules.Any(Holds)
            : condition.Rules.All(Holds);

        /// <summary>§5.4: los textos se comparan sin tildes, mayúsculas ni espacios de más; los números, en cultura invariante. Un valor que
        /// no es número hace falsa la comparación numérica, en los dos sentidos.</summary>
        private bool Holds(ConditionRule rule) => rule.Operator switch
        {
            "isEmpty" => IsEmpty(variables.Get(rule.Variable)),
            "isNotEmpty" => !IsEmpty(variables.Get(rule.Variable)),
            "equals" => TextMatch.Same(variables.TextOf(rule.Variable), rule.Value),
            "notEquals" => !TextMatch.Same(variables.TextOf(rule.Variable), rule.Value),
            "contains" => TextMatch.Contains(variables.TextOf(rule.Variable), rule.Value),
            "greaterThan" => Compare(variables.TextOf(rule.Variable), rule.Value) > 0,
            "lessThan" => Compare(variables.TextOf(rule.Variable), rule.Value) < 0,
            "countEquals" => Number(rule.Value) is { } count && Count(variables.Get(rule.Variable)) == count,
            "countGreaterThan" => Number(rule.Value) is { } count && Count(variables.Get(rule.Variable)) > count,
            _ => false,
        };

        /// <summary>Vacío es ausente, <c>null</c>, texto en blanco, o lista u objeto sin elementos: así «¿Cliente encontrado?» da «no»
        /// también cuando la API devolvió <c>{}</c> o <c>[]</c>.</summary>
        private static bool IsEmpty(JsonNode? node) => node switch
        {
            null => true,
            JsonArray array => array.Count == 0,
            JsonObject obj => obj.Count == 0,
            JsonValue value when value.TryGetValue<string>(out var text) => string.IsNullOrWhiteSpace(text),
            _ => false,
        };

        /// <summary>El largo de una lista; cualquier otra cosa cuenta 0.</summary>
        private static int Count(JsonNode? node) => node is JsonArray array ? array.Count : 0;

        private static int? Compare(string left, string? right) =>
            Number(left) is { } a && Number(right) is { } b ? a.CompareTo(b) : null;

        private static decimal? Number(string? text) =>
            decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
