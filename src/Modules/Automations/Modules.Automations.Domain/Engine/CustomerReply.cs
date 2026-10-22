using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Modules.Automations.Domain.Flow;
using Modules.Automations.Domain.Text;

namespace Modules.Automations.Domain.Engine;

/// <summary>
/// Spec 2026-10-21 §5.4, D-I5 y D-M7: qué significa un <see cref="EngineInput.CustomerMessage"/> para el bloque que espera. Dice por qué
/// salida sigue y qué guarda en <c>saveTo</c>, o que la respuesta no vale (un intento). No decide qué pasa con un intento: eso es del motor.
/// </summary>
internal static class CustomerReply
{
    /// <summary>D-I1: Meta no manda una lista con más de diez filas (contando la <c>extraOption</c>).</summary>
    public const int MaxListRows = 10;

    // El mismo tope del validador (FlowValidator.PatternTimeout): una expresión del admin no puede trabar el worker.
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    public static ReplyOutcome Classify(FlowNode waiting, EngineInput.CustomerMessage message, RunVariables variables)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(variables);

        return waiting switch
        {
            OpenQuestionNode question => Answer(question, message),
            ButtonsNode buttons => Choose(buttons, message),
            ListNode list => Choose(list, message, variables),
            AttachmentNode attachment => Receive(attachment, message),
            _ => throw new ArgumentOutOfRangeException(nameof(waiting), waiting.Kind, "El bloque no espera al cliente."),
        };
    }

    /// <summary>
    /// §5.4: las filas fijas, o con <c>dynamic</c> una por elemento de la colección (sin id o sin título se salta), y al final la
    /// <c>extraOption</c>. El motor arma con ellas el mensaje al entrar y <see cref="Classify"/> compara contra las mismas, así que las dos
    /// cosas ven siempre las mismas filas.
    /// </summary>
    public static IReadOnlyList<ListRow> RowsOf(ListNode list, RunVariables variables)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(variables);

        var options = list.Options;
        var rows = new List<ListRow>();
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
                        rows.Add(new ListRow(id, title, description.Length > 0 ? description : null, item, IsExtra: false));
                    }
                }
            }
        }
        else
        {
            rows.AddRange((options.Fixed ?? []).Select(row => new ListRow(row.Id, row.Title, row.Description, null, IsExtra: false)));
        }

        if (options.ExtraOption is { } extra)
        {
            rows.Add(new ListRow(extra.Id, extra.Title, extra.Description, null, IsExtra: true));
        }

        return rows;
    }

    private static string Field(JsonNode? item, string? field) =>
        field is null ? string.Empty : RunVariables.FormatText(JsonPath.Select(item, field)).Trim();

    /// <summary>Pregunta abierta: sólo texto. Se guarda recortado; con <c>number</c>, como número para que una condición o una API lo lean
    /// como tal.</summary>
    private static ReplyOutcome Answer(OpenQuestionNode question, EngineInput.CustomerMessage message)
    {
        var text = message.Text?.Trim();
        if (message.Kind != CustomerMessageKinds.Text || string.IsNullOrEmpty(text))
        {
            return ReplyOutcome.Invalid.Instance;
        }

        var validation = question.Validation;
        JsonNode? saved = validation.Kind switch
        {
            QuestionValidationKinds.None => JsonValue.Create(text),
            QuestionValidationKinds.Digits => IsDigits(text) ? JsonValue.Create(text) : null,
            QuestionValidationKinds.Number => Number(text) is { } number ? JsonValue.Create(number) : null,
            QuestionValidationKinds.Regex => Matches(text, validation.Pattern) ? JsonValue.Create(text) : null,
            QuestionValidationKinds.Keywords => TextMatch.IsAnyOf(text, validation.Keywords ?? []) ? JsonValue.Create(text) : null,
            _ => null,
        };

        return saved is null ? ReplyOutcome.Invalid.Instance : new ReplyOutcome.Output(FlowOutputs.Answered, saved);
    }

    /// <summary>El <c>^\d+$</c> del brief, sólo con dígitos ASCII: <c>\d</c> de .NET también acepta dígitos de otras escrituras («١٢٣»),
    /// que una cédula no lleva y que la API del cliente no entendería.</summary>
    private static bool IsDigits(string text) => text.All(char.IsAsciiDigit);

    /// <summary>Invariante, con la coma como separador decimal también («1234,5»): es como lo escribe un cliente en Colombia. Sin
    /// separadores de miles ni exponente, que harían ambiguo «1,234».</summary>
    private static decimal? Number(string text) =>
        decimal.TryParse(
            text.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>Un patrón inválido (un borrador en el simulador; el validador no lo publica) o que tarda más de 100 ms no valida: la
    /// respuesta cuenta como intento y el paso nunca lanza.</summary>
    private static bool Matches(string text, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>D-I5: con <c>ReplyId</c> (el cliente tocó un botón) se compara sólo el id; un id que no es de este bloque (un botón de un
    /// mensaje viejo) no vale aunque el título coincida. Sin id, el texto escrito se compara con las etiquetas.</summary>
    private static ReplyOutcome Choose(ButtonsNode node, EngineInput.CustomerMessage message)
    {
        var button = message.ReplyId is { } replyId
            ? node.Buttons.FirstOrDefault(candidate => candidate.Id == replyId)
            : node.Buttons.FirstOrDefault(candidate => TextMatch.Same(message.Text, candidate.Label));

        if (button is null)
        {
            return ReplyOutcome.Invalid.Instance;
        }

        var saved = string.IsNullOrWhiteSpace(node.SaveTo) ? null : new JsonObject { ["id"] = button.Id, ["title"] = button.Label };
        return new ReplyOutcome.Output(button.Id, saved);
    }

    /// <summary>Como en Botones (D-I5). La fila elegida guarda <c>{ id, title }</c>, y en una lista dinámica también el elemento entero de
    /// la colección (<c>item</c>), para que los bloques que siguen lean cualquier campo del equipo o la dirección elegida. La
    /// <c>extraOption</c> no guarda nada: no es un elemento.</summary>
    private static ReplyOutcome Choose(ListNode node, EngineInput.CustomerMessage message, RunVariables variables)
    {
        var rows = RowsOf(node, variables);
        var row = message.ReplyId is { } replyId
            ? rows.FirstOrDefault(candidate => candidate.Id == replyId)
            : rows.FirstOrDefault(candidate => TextMatch.Same(message.Text, candidate.Title));

        if (row is null)
        {
            return ReplyOutcome.Invalid.Instance;
        }

        if (row.IsExtra)
        {
            return new ReplyOutcome.Output(FlowOutputs.Extra, null);
        }

        var saved = new JsonObject { ["id"] = row.Id, ["title"] = row.Title };
        if (row.Item is not null)
        {
            saved["item"] = row.Item.DeepClone();
        }

        return new ReplyOutcome.Output(FlowOutputs.Chosen, saved);
    }

    /// <summary>§5.4: guarda los datos del adjunto, no el archivo. <c>url</c> no está aquí: la completa el procesador cuando el archivo ya
    /// está en R2 (D-H8, tarea 6).</summary>
    private static ReplyOutcome Receive(AttachmentNode node, EngineInput.CustomerMessage message)
    {
        if (message.Attachment is not { } attachment || !Accepts(node.Accept, message.Kind))
        {
            return ReplyOutcome.Invalid.Instance;
        }

        var saved = new JsonObject
        {
            ["messageId"] = attachment.MessageId.ToString(),
            ["fileName"] = attachment.FileName,
            ["mimeType"] = attachment.MimeType,
            ["stored"] = attachment.Stored,
        };
        return new ReplyOutcome.Output(FlowOutputs.Received, saved);
    }

    private static bool Accepts(string accept, string kind) => accept switch
    {
        AttachmentAccepts.Image => kind == CustomerMessageKinds.Image,
        AttachmentAccepts.Document => kind == CustomerMessageKinds.Document,
        AttachmentAccepts.Any => kind is CustomerMessageKinds.Image or CustomerMessageKinds.Document or CustomerMessageKinds.Video
            or CustomerMessageKinds.Audio,
        _ => false,
    };
}

/// <summary>Lo que <see cref="CustomerReply.Classify"/> concluye de la respuesta del cliente.</summary>
internal abstract record ReplyOutcome
{
    private ReplyOutcome()
    {
    }

    /// <summary>La respuesta vale: el bloque sale por <see cref="Name"/> y, si <see cref="Saved"/> no es <c>null</c>, lo guarda en su
    /// <c>saveTo</c>.</summary>
    public sealed record Output(string Name, JsonNode? Saved) : ReplyOutcome;

    /// <summary>No es lo que el bloque espera (D-M7): cuenta como intento.</summary>
    public sealed record Invalid : ReplyOutcome
    {
        public static readonly Invalid Instance = new();
    }
}

/// <summary>Una fila de la Lista; <see cref="Item"/> es el elemento de la colección en una dinámica.</summary>
internal sealed record ListRow(string Id, string Title, string? Description, JsonNode? Item, bool IsExtra);
