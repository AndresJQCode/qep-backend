using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Modules.Automations.Domain.Flow;

/// <summary>
/// Spec 2026-10-21 §4.3: lo que impide publicar un flujo (errores) y lo que conviene revisar (avisos). Es puro: lo que depende
/// del tenant llega en <see cref="FlowValidationContext"/>. Los mensajes son para el admin, en el panel de Validar del editor.
/// </summary>
public static partial class FlowValidator
{
    // Meta: un texto libre llega hasta 4096; el cuerpo de un interactivo (botones, lista, botón URL), hasta 1024 (D-I1).
    private const int TextMaxLength = 4096;
    private const int InteractiveBodyMaxLength = 1024;

    // D-I1: los demás límites de Meta para interactivos.
    private const int MaxButtons = 3;
    private const int LabelMaxLength = 20;
    private const int MaxRows = 10;
    private const int RowTitleMaxLength = 24;
    private const int RowDescriptionMaxLength = 72;

    // El nombre de ConversationChannel.Instagram (Modules.Messaging.Domain/MessageEnums.cs): Instagram no tiene listas (D-M6).
    private const string Instagram = "Instagram";

    private const string NameRule = "empieza con minúscula y usa sólo letras y números, máximo 40.";

    /// <summary>D-M8: las variables que el motor siempre llena, sin que un bloque las escriba.</summary>
    private static readonly string[] FixedOrder = ["nombre", "telefono", "empresa", "conexion"];

    private static readonly FrozenSet<string> FixedVariables = FixedOrder.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Sin conectar, D-M7 las cubre en tiempo de ejecución (reinicia o repite), así que son aviso y no error.</summary>
    private static readonly FrozenSet<string> OptionalOutputs =
        new[] { FlowOutputs.Invalid, FlowOutputs.Other, FlowOutputs.TooMany }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Cómo el editor rotula cada salida (qep-frontend, editor de chatbots); las de Botones son la etiqueta del botón.</summary>
    private static readonly FrozenDictionary<string, string> OutputLabels = new Dictionary<string, string>
    {
        [FlowOutputs.Next] = "sigue",
        [FlowOutputs.Answered] = "respondió",
        [FlowOutputs.Invalid] = "no válido",
        [FlowOutputs.Other] = "otra respuesta",
        [FlowOutputs.Chosen] = "eligió",
        [FlowOutputs.Extra] = "opción extra",
        [FlowOutputs.TooMany] = "demasiadas",
        [FlowOutputs.Received] = "recibió",
        [FlowOutputs.Yes] = "sí",
        [FlowOutputs.No] = "no",
        [FlowOutputs.Open] = "abierto",
        [FlowOutputs.Closed] = "cerrado",
        [FlowOutputs.Ok] = "ok",
        [FlowOutputs.Error] = "error",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IReadOnlyList<FlowIssue> Validate(FlowDefinition flow, FlowValidationContext context)
    {
        var issues = new List<FlowIssue>();
        var ids = flow.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);

        var startIsValid = CheckStart(flow.Start, ids, issues);
        foreach (var variable in flow.Variables)
        {
            if (!VariableName().IsMatch(variable.Name))
            {
                issues.Add(Error(
                    FlowIssueCodes.VariableNameInvalid, null,
                    $"La variable declarada «{variable.Name}» no tiene un nombre válido: {NameRule}"));
            }
        }

        var written = WrittenVariables(flow);
        foreach (var node in flow.Nodes)
        {
            CheckOutputs(node, ids, issues);
            CheckNode(node, context, issues);
            CheckReferences(node, written, issues);
        }

        if (startIsValid)
        {
            CheckReachability(flow, ids, issues);
        }

        return issues;
    }

    /// <summary>Las variables que el editor ofrece para interpolar: las fijas (D-M8), las declaradas y las que escribe algún bloque,
    /// sin repetir y en ese orden. Las vacías (un <c>saveTo</c> a medio escribir) no cuentan.</summary>
    public static IReadOnlyList<string> KnownVariables(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var known = new List<string>(FixedVariables.Count + flow.Variables.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in FixedOrder.Concat(flow.Variables.Select(variable => variable.Name)).Concat(WrittenInOrder(flow)))
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                known.Add(name);
            }
        }

        return known;
    }

    private static bool CheckStart(FlowStart start, HashSet<string> ids, List<FlowIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(start.FirstNodeId))
        {
            issues.Add(Error(FlowIssueCodes.StartNoFirstNode, null, "El inicio no está conectado: conéctalo al primer bloque del flujo."));
            return false;
        }

        if (!ids.Contains(start.FirstNodeId))
        {
            issues.Add(Error(FlowIssueCodes.NodeUnknownTarget, null, "El inicio apunta a un bloque que no existe."));
            return false;
        }

        return true;
    }

    private static void CheckOutputs(FlowNode node, HashSet<string> ids, List<FlowIssue> issues)
    {
        foreach (var output in FlowNodeKinds.OutputsOf(node))
        {
            var target = node.Outputs.GetValueOrDefault(output);
            if (target is null)
            {
                var message = $"La salida «{OutputLabel(node, output)}» de «{node.Title}» no está conectada.";
                issues.Add(OptionalOutputs.Contains(output)
                    ? Warning(FlowIssueCodes.NodeOutputUnconnectedOptional, node.Id, message)
                    : Error(FlowIssueCodes.NodeOutputUnconnected, node.Id, message));
            }
            else if (!ids.Contains(target))
            {
                issues.Add(Error(
                    FlowIssueCodes.NodeUnknownTarget, node.Id,
                    $"La salida «{OutputLabel(node, output)}» de «{node.Title}» apunta a un bloque que no existe."));
            }
        }
    }

    private static string OutputLabel(FlowNode node, string output) => node switch
    {
        ButtonsNode buttons when buttons.Buttons.FirstOrDefault(button => button.Id == output) is { } button => button.Label,
        ListNode { Options.ExtraOption: { } extra } when output == FlowOutputs.Extra => extra.Title,
        _ => OutputLabels.GetValueOrDefault(output, output),
    };

    private static void CheckNode(FlowNode node, FlowValidationContext context, List<FlowIssue> issues)
    {
        switch (node)
        {
            case MessageNode message:
                CheckText(node, message.Text, message.UrlButton is null ? TextMaxLength : InteractiveBodyMaxLength, issues);
                if (message.UrlButton is { } urlButton)
                {
                    CheckLabel(node, urlButton.Label, issues);
                }

                break;
            case OpenQuestionNode question:
                CheckText(node, question.Text, TextMaxLength, issues);
                CheckName(node, question.SaveTo, issues);
                break;
            case ButtonsNode buttons:
                CheckText(node, buttons.Text, InteractiveBodyMaxLength, issues);
                if (buttons.Buttons.Count > MaxButtons)
                {
                    issues.Add(Error(
                        FlowIssueCodes.ButtonsTooMany, node.Id,
                        $"«{node.Title}» tiene {buttons.Buttons.Count} botones; WhatsApp permite máximo {MaxButtons}. Usa una lista si necesitas más."));
                }

                foreach (var button in buttons.Buttons)
                {
                    CheckLabel(node, button.Label, issues);
                }

                CheckButtonIds(buttons, issues);

                // Guardar la respuesta de Botones es opcional: un saveTo en blanco es «no guardar».
                if (!string.IsNullOrWhiteSpace(buttons.SaveTo))
                {
                    CheckName(node, buttons.SaveTo, issues);
                }

                break;
            case ListNode list:
                CheckList(list, context, issues);
                break;
            case AttachmentNode attachment:
                CheckText(node, attachment.Text, TextMaxLength, issues);
                CheckName(node, attachment.SaveTo, issues);
                break;
            case ConditionNode condition:
                if (condition.Rules.Count == 0)
                {
                    issues.Add(Error(FlowIssueCodes.ConditionNoRules, node.Id, $"«{node.Title}» no tiene reglas: agrega al menos una."));
                }

                // La regla nombra la variable sin llaves y puede entrar con puntos o índices (cliente.id): se valida su raíz.
                foreach (var rule in condition.Rules)
                {
                    CheckName(node, RootOf(rule.Variable), issues);
                }

                break;
            case BusinessHoursNode when !context.AllConnectionsHaveBusinessHours:
                issues.Add(Warning(
                    FlowIssueCodes.BusinessHoursNotAssigned, node.Id,
                    $"«{node.Title}» consulta el horario de atención, y no todas las conexiones del chatbot tienen uno asignado."));
                break;
            case HttpNode http:
                CheckHttp(http, context, issues);
                break;
            case HandoffNode { Text: { } text }:
                CheckOptionalText(node, text, issues);
                break;
            case ResolveNode { Text: { } text }:
                CheckOptionalText(node, text, issues);
                break;
        }
    }

    private static void CheckList(ListNode list, FlowValidationContext context, List<FlowIssue> issues)
    {
        CheckText(list, list.Text, InteractiveBodyMaxLength, issues);
        CheckLabel(list, list.ButtonLabel, issues);
        CheckName(list, list.SaveTo, issues);

        if (context.Channels.Contains(Instagram))
        {
            issues.Add(Error(
                FlowIssueCodes.ListChannelNotSupported, list.Id,
                $"«{list.Title}» es una lista, e Instagram no tiene listas: usa botones o una pregunta."));
        }

        // Con filas fijas el tope de Meta se conoce al guardar (la fila extra también cuenta); con filas dinámicas lo
        // resuelve la salida «demasiadas» en tiempo de ejecución.
        IReadOnlyList<ListFixedRow> fixedRows = list.Options.Kind == ListOptionKinds.Fixed ? list.Options.Fixed ?? [] : [];
        IReadOnlyList<ListFixedRow> rows = list.Options.ExtraOption is { } extra ? [.. fixedRows, extra] : fixedRows;
        if (list.Options.Kind == ListOptionKinds.Fixed && rows.Count > MaxRows)
        {
            issues.Add(Error(
                FlowIssueCodes.ListTooManyRows, list.Id,
                $"La lista de «{list.Title}» tiene {rows.Count} filas; WhatsApp permite máximo {MaxRows}."));
        }

        CheckRowIds(list, rows, issues);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Title))
            {
                issues.Add(Error(FlowIssueCodes.TextEmpty, list.Id, $"Una fila de «{list.Title}» no tiene título."));
            }
            else if (row.Title.Length > RowTitleMaxLength)
            {
                issues.Add(Error(
                    FlowIssueCodes.ListRowTitleTooLong, list.Id,
                    $"El título «{row.Title}» de «{list.Title}» tiene {row.Title.Length} caracteres; WhatsApp permite máximo {RowTitleMaxLength}."));
            }

            if (row.Description is { Length: > RowDescriptionMaxLength } description)
            {
                issues.Add(Error(
                    FlowIssueCodes.ListRowDescriptionTooLong, list.Id,
                    $"La descripción de la fila «{row.Title}» de «{list.Title}» tiene {description.Length} caracteres; WhatsApp permite máximo {RowDescriptionMaxLength}."));
            }
        }
    }

    private static void CheckHttp(HttpNode http, FlowValidationContext context, List<FlowIssue> issues)
    {
        // D-C14: una plantilla instalada trae connectionId null hasta que el admin elige su conexión.
        if (http.ConnectionId is not { } connectionId)
        {
            issues.Add(Error(FlowIssueCodes.HttpConnectionMissing, http.Id, $"Elige la conexión que usa «{http.Title}»."));
        }
        else if (context.PausedExternalApis.Contains(connectionId))
        {
            issues.Add(Error(
                FlowIssueCodes.HttpConnectionPaused, http.Id,
                $"La conexión de «{http.Title}» está pausada: reactívala en Integraciones o elige otra."));
        }
        else if (!context.ActiveExternalApis.Contains(connectionId))
        {
            issues.Add(Error(
                FlowIssueCodes.HttpConnectionMissing, http.Id,
                $"La conexión de «{http.Title}» ya no existe o no es una API externa: elige otra."));
        }

        // La URL base es de la conexión (D-H2 la valida y resuelve); la ruta sólo puede ser relativa a ella.
        if (!HttpPath().IsMatch(http.Path))
        {
            issues.Add(Error(
                FlowIssueCodes.HttpPathInvalid, http.Id,
                $"La ruta de «{http.Title}» tiene que empezar con / y no llevar dominio, espacios, ? ni #: la query va aparte."));
        }

        if (http.Body is { Kind: HttpBodyKinds.Json } body && !IsJsonTemplate(body.Template))
        {
            issues.Add(Error(
                FlowIssueCodes.HttpBodyNotJson, http.Id,
                $"El cuerpo de «{http.Title}» no es un JSON válido. Las variables van como {{variable}} donde iría un valor."));
        }

        foreach (var mapping in http.Mapping)
        {
            CheckName(http, mapping.To, issues);
        }

        if (http.SampleResponse is null)
        {
            issues.Add(Warning(
                FlowIssueCodes.HttpNoSampleResponse, http.Id,
                $"«{http.Title}» no tiene respuesta de ejemplo: el simulador sólo lo puede probar llamando a la API real."));
        }
    }

    // D-H4 «validado como JSON al guardar»: cada variable se reemplaza por 0, que es JSON válido suelto ({cliente.id}) y entre
    // comillas ("{cedula}"), y lo que queda tiene que leerse como JSON.
    private static bool IsJsonTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(VariableToken().Replace(template, "0"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void CheckText(FlowNode node, string text, int maxLength, List<FlowIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            issues.Add(Error(FlowIssueCodes.TextEmpty, node.Id, $"El texto de «{node.Title}» está vacío."));
        }
        else if (text.Length > maxLength)
        {
            issues.Add(Error(
                FlowIssueCodes.TextTooLong, node.Id,
                $"El texto de «{node.Title}» tiene {text.Length} caracteres; el máximo es {maxLength}."));
        }
    }

    // Pasar a un asesor y Resolver pueden no decir nada; si dicen algo, es texto libre.
    private static void CheckOptionalText(FlowNode node, string text, List<FlowIssue> issues)
    {
        if (text.Length > TextMaxLength)
        {
            issues.Add(Error(
                FlowIssueCodes.TextTooLong, node.Id,
                $"El texto de «{node.Title}» tiene {text.Length} caracteres; el máximo es {TextMaxLength}."));
        }
    }

    private static void CheckLabel(FlowNode node, string label, List<FlowIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            issues.Add(Error(FlowIssueCodes.TextEmpty, node.Id, $"Un botón de «{node.Title}» no tiene etiqueta."));
        }
        else if (label.Length > LabelMaxLength)
        {
            issues.Add(Error(
                FlowIssueCodes.ButtonsLabelTooLong, node.Id,
                $"La etiqueta «{label}» de «{node.Title}» tiene {label.Length} caracteres; WhatsApp permite máximo {LabelMaxLength}."));
        }
    }

    private static void CheckButtonIds(ButtonsNode buttons, List<FlowIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var button in buttons.Buttons)
        {
            // «other» es la salida de la respuesta no esperada: un botón con ese id la taparía.
            if (!OptionId().IsMatch(button.Id) || button.Id == FlowOutputs.Other || !seen.Add(button.Id))
            {
                issues.Add(Error(
                    FlowIssueCodes.ButtonsIdInvalid, buttons.Id,
                    $"El botón «{button.Label}» de «{buttons.Title}» tiene un id vacío, repetido o reservado: vuelve a crearlo."));
            }
        }
    }

    private static void CheckRowIds(ListNode list, IReadOnlyList<ListFixedRow> rows, List<FlowIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!OptionId().IsMatch(row.Id) || !seen.Add(row.Id))
            {
                issues.Add(Error(
                    FlowIssueCodes.ListRowIdInvalid, list.Id,
                    $"La fila «{row.Title}» de «{list.Title}» tiene un id vacío o repetido: vuelve a crearla."));
            }
        }
    }

    private static void CheckName(FlowNode node, string name, List<FlowIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(Error(FlowIssueCodes.VariableNameInvalid, node.Id, $"Falta el nombre de una variable en «{node.Title}»."));
        }
        else if (!VariableName().IsMatch(name))
        {
            issues.Add(Error(
                FlowIssueCodes.VariableNameInvalid, node.Id,
                $"«{name}» no es un nombre de variable válido en «{node.Title}»: {NameRule}"));
        }
    }

    private static HashSet<string> WrittenVariables(FlowDefinition flow)
    {
        // D-M8: cuenta lo que produce un bloque. Declarar una variable en variables[] no la llena.
        var written = new HashSet<string>(FixedVariables, StringComparer.Ordinal);
        written.UnionWith(WrittenInOrder(flow));
        return written;
    }

    private static IEnumerable<string> WrittenInOrder(FlowDefinition flow)
    {
        foreach (var node in flow.Nodes)
        {
            switch (node)
            {
                case OpenQuestionNode question:
                    yield return question.SaveTo;
                    break;
                case ButtonsNode { SaveTo: { } saveTo } when !string.IsNullOrWhiteSpace(saveTo):
                    yield return saveTo;
                    break;
                case ListNode list:
                    yield return list.SaveTo;
                    break;
                case AttachmentNode attachment:
                    yield return attachment.SaveTo;
                    break;
                case HttpNode http:
                    foreach (var mapping in http.Mapping)
                    {
                        yield return mapping.To;
                    }

                    break;
            }
        }
    }

    // D-M8 pide avisar si «no la produce ningún bloque anterior». Aquí se simplifica a «ningún bloque»: saber qué bloque va antes
    // exige recorrer cada camino, y una variable escrita en otra rama sigue sin aviso. Las referencias con puntos o índices
    // ({cliente.id}, {lista[0].id}) cuentan por su raíz.
    private static void CheckReferences(FlowNode node, HashSet<string> written, List<FlowIssue> issues)
    {
        var roots = new List<string>();
        foreach (var text in InterpolatedTexts(node))
        {
            if (text is not null)
            {
                roots.AddRange(VariableToken().Matches(text).Select(match => RootOf(match.Groups["reference"].Value)));
            }
        }

        roots.AddRange(DirectReferences(node).Select(RootOf));

        // Una raíz en blanco (la regla sin variable) ya es variable.name_invalid.
        foreach (var root in roots.Distinct(StringComparer.Ordinal).Where(root => root.Length > 0 && !written.Contains(root)))
        {
            issues.Add(Warning(
                FlowIssueCodes.VariableNeverSet, node.Id,
                $"«{node.Title}» usa la variable {{{root}}}, y ningún bloque la guarda: llegará vacía."));
        }
    }

    /// <summary>D-M8: las variables se interpolan en todo texto, ruta, query y cuerpo.</summary>
    private static IEnumerable<string?> InterpolatedTexts(FlowNode node) => node switch
    {
        MessageNode message => [message.Text, message.UrlButton?.Url],
        OpenQuestionNode question => [question.Text],
        ButtonsNode buttons => [buttons.Text],
        ListNode list => [list.Text],
        AttachmentNode attachment => [attachment.Text],
        ConditionNode condition => condition.Rules.Select(rule => rule.Value),
        HttpNode http =>
        [
            http.Path,
            .. http.Query.Select(pair => pair.Value),
            http.Body?.Template,
            .. (http.Body?.Fields ?? []).Select(pair => pair.Value),
        ],
        HandoffNode handoff => [handoff.Text],
        ResolveNode resolve => [resolve.Text],
        _ => [],
    };

    /// <summary>Los campos que nombran una variable sin llaves.</summary>
    private static IEnumerable<string> DirectReferences(FlowNode node) => node switch
    {
        ConditionNode condition => condition.Rules.Select(rule => rule.Variable),
        ListNode { Options.Collection: { } collection } => [collection],
        HttpNode { Body.FileVariable: { } fileVariable } => [fileVariable],
        _ => [],
    };

    private static string RootOf(string reference)
    {
        var end = reference.IndexOfAny(['.', '[']);
        return end < 0 ? reference : reference[..end];
    }

    private static void CheckReachability(FlowDefinition flow, HashSet<string> ids, List<FlowIssue> issues)
    {
        // FlowJson rechaza ids repetidos, pero un FlowDefinition armado a mano puede traerlos: gana el primero.
        var byId = new Dictionary<string, FlowNode>(StringComparer.Ordinal);
        foreach (var node in flow.Nodes)
        {
            byId.TryAdd(node.Id, node);
        }

        var reached = new HashSet<string>(StringComparer.Ordinal) { flow.Start.FirstNodeId! };
        var pending = new Queue<string>(reached);
        while (pending.TryDequeue(out var id))
        {
            var node = byId[id];
            foreach (var output in FlowNodeKinds.OutputsOf(node))
            {
                if (node.Outputs.GetValueOrDefault(output) is { } target && ids.Contains(target) && reached.Add(target))
                {
                    pending.Enqueue(target);
                }
            }
        }

        foreach (var node in flow.Nodes.Where(node => !reached.Contains(node.Id)))
        {
            issues.Add(Warning(FlowIssueCodes.NodeUnreachable, node.Id, $"Ningún camino desde el inicio llega a «{node.Title}»."));
        }
    }

    private static FlowIssue Error(string code, string? nodeId, string message) => new(FlowIssueSeverity.Error, code, nodeId, message);

    private static FlowIssue Warning(string code, string? nodeId, string message) => new(FlowIssueSeverity.Warning, code, nodeId, message);

    // Anexo A.
    [GeneratedRegex(@"^[a-z][a-zA-Z0-9]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VariableName();

    [GeneratedRegex(@"\{(?<reference>[a-zA-Z][a-zA-Z0-9_.\[\]]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariableToken();

    // Anexo A: la forma de los ids de botón y de fila.
    [GeneratedRegex(@"^[a-zA-Z0-9_-]{1,40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex OptionId();

    // Empieza con una sola barra (dos serían un host: //otro.com) y no trae espacios, query ni fragmento.
    [GeneratedRegex(@"^/(?!/)[^\s?#]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex HttpPath();
}
