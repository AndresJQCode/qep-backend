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
            EngineInput.CustomerMessage => throw new NotImplementedException("La respuesta del cliente a un bloque que espera llega en la tarea 3 del plan 2."),
            _ => throw new ArgumentOutOfRangeException(nameof(input), input.GetType().Name, "Entrada del motor desconocida."),
        };
    }

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
        private readonly RunVariables variables;
        private readonly DateTimeOffset now;
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

                if (!nodes.TryGetValue(targetId, out var node))
                {
                    throw new InvalidOperationException($"El flujo apunta al bloque «{targetId}», que no existe.");
                }

                // Dentro de un paso nada cambia las variables, así que volver a un bloque sin pasar por una espera repetiría el mismo
                // recorrido para siempre (y acumularía envíos sin fin). El validador no rechaza esos ciclos al publicar.
                if (!entered.Add(node.Id))
                {
                    throw new InvalidOperationException($"El flujo vuelve al bloque «{node.Id}» sin esperar al cliente: daría vueltas para siempre.");
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
                    effects.Add(Send(message));
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
                case OpenQuestionNode or ButtonsNode or ListNode or AttachmentNode:
                    effects.Add(Prompt(node));
                    effects.Add(new Effect.WaitForCustomer(node.Id, node.Title));
                    return Stop(node, RunStatus.Waiting, TraceOutcomes.Waiting);
                default:
                    throw new ArgumentOutOfRangeException(nameof(node), node.Kind, "Bloque sin semántica en el motor.");
            }
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
            if (trace.Count > RunState.TraceMax)
            {
                trace.RemoveAt(0);
            }
        }

        private StepResult Result() =>
            new(new RunState(status, currentNodeId, stepNumber, attempts, variables, [.. trace]), [.. effects]);

        private string Text(string template) => TemplateText.Interpolate(template, variables);

        /// <summary>D-I1: con botón de URL sale como <c>CtaUrl</c>, que es la única forma de mandar un enlace como botón.</summary>
        private Effect Send(MessageNode message) => message.UrlButton is { } button
            ? new Effect.SendInteractive(message.Id, new InteractiveMessage.CtaUrl(Text(message.Text), Text(button.Label), Text(button.Url)))
            : new Effect.SendText(message.Id, Text(message.Text));

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
        /// El mensaje de un bloque que espera. Los títulos de botones y filas no se interpolan: la tarea 3 compara contra ellos lo que el
        /// cliente escribe.
        /// </summary>
        private Effect Prompt(FlowNode node) => node switch
        {
            OpenQuestionNode question => new Effect.SendText(node.Id, Text(question.Text)),
            AttachmentNode attachment => new Effect.SendText(node.Id, Text(attachment.Text)),
            ButtonsNode buttons => new Effect.SendInteractive(node.Id, new InteractiveMessage.Buttons(
                Text(buttons.Text), [.. buttons.Buttons.Select(button => (button.Id, button.Label))])),
            ListNode list => new Effect.SendInteractive(node.Id, new InteractiveMessage.List(Text(list.Text), list.ButtonLabel, Rows(list))),
            _ => throw new ArgumentOutOfRangeException(nameof(node), node.Kind, "El bloque no espera al cliente."),
        };

        /// <summary>
        /// §5.4: las filas fijas, o con <c>dynamic</c> una por elemento de la colección (sin id o sin título se salta), y al final la
        /// <c>extraOption</c>. El tope de 10 y la lista vacía (salida <c>tooMany</c>, sin enviar) son de la tarea 3.
        /// </summary>
        private List<(string Id, string Title, string? Description)> Rows(ListNode list)
        {
            var options = list.Options;
            var rows = new List<(string Id, string Title, string? Description)>();
            if (options.Kind == ListOptionKinds.Dynamic)
            {
                if (variables.Get(options.Collection ?? string.Empty) is JsonArray items)
                {
                    foreach (var item in items)
                    {
                        var id = Field(item, options.IdField);
                        var title = Field(item, options.TitleField);
                        if (id.Length > 0 && title.Length > 0)
                        {
                            var description = Field(item, options.DescriptionField);
                            rows.Add((id, title, description.Length > 0 ? description : null));
                        }
                    }
                }
            }
            else
            {
                rows.AddRange((options.Fixed ?? []).Select(row => (row.Id, row.Title, row.Description)));
            }

            if (options.ExtraOption is { } extra)
            {
                rows.Add((extra.Id, extra.Title, extra.Description));
            }

            return rows;
        }

        private static string Field(JsonNode? item, string? field) =>
            field is null ? string.Empty : RunVariables.FormatText(JsonPath.Select(item, field)).Trim();

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
