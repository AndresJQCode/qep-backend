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
internal static partial class CustomerReply
{
    /// <summary>D-I1: Meta no manda una lista con más de diez filas (contando la <c>extraOption</c>).</summary>
    public const int MaxListRows = 10;

    // Ruling P2-R15: los topes de una fila que valida Messaging (InteractiveSend.cs:30, InteractiveLimits) y que Meta exige; cuentan
    // unidades UTF-16 (string.Length), así que el recorte también.
    private const int RowIdMax = 200;
    private const int RowTitleMax = 24;
    private const int RowDescriptionMax = 72;
    private const string Ellipsis = "…";

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
            DataConsentNode => Choose(DataConsentButtons.All, message),
            _ => throw new ArgumentOutOfRangeException(nameof(waiting), waiting.Kind, "El bloque no espera al cliente."),
        };
    }

    /// <summary>
    /// §5.4: las filas fijas, o con <c>dynamic</c> una por elemento de la colección (sin id o sin título se salta), y al final la
    /// <c>extraOption</c>. El motor arma con ellas el mensaje al entrar y <see cref="Classify"/> compara contra las mismas, así que las dos
    /// cosas ven siempre las mismas filas.
    /// Ruling P2-R15: los datos de una API no respetan los topes de Meta, y una fila fuera de tope hace que Messaging rechace el envío y la
    /// corrida termine Failed. Por eso cada fila se ajusta (título y descripción recortados, id largo fuera) y un id repetido se descarta
    /// (gana el primero; si choca con la <c>extraOption</c>, gana la extra). Todo antes de contar las diez.
    /// </summary>
    public static IReadOnlyList<ListRow> RowsOf(ListNode list, RunVariables variables)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(variables);

        var options = list.Options;
        var candidates = new List<(string Id, string Title, string? Description, JsonNode? Item)>();
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
                        candidates.Add((id, title, description.Length > 0 ? description : null, item));
                    }
                }
            }
        }
        else
        {
            candidates.AddRange((options.Fixed ?? []).Select(row => (row.Id, row.Title, row.Description, (JsonNode?)null)));
        }

        var extra = options.ExtraOption;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (extra is not null)
        {
            seen.Add(extra.Id);
        }

        var rows = new List<ListRow>();
        foreach (var (id, title, description, item) in candidates)
        {
            if (id.Length <= RowIdMax && seen.Add(id))
            {
                rows.Add(Row(id, title, description, item, isExtra: false));
            }
        }

        if (extra is not null)
        {
            rows.Add(Row(extra.Id, extra.Title, extra.Description, null, isExtra: true));
        }

        return rows;
    }

    private static ListRow Row(string id, string title, string? description, JsonNode? item, bool isExtra) =>
        new(id, Fit(title, RowTitleMax), description is null ? null : Fit(description, RowDescriptionMax), title, item, isExtra);

    /// <summary>
    /// Recorta a <paramref name="max"/> unidades UTF-16 contando el «…», sin partir un elemento de texto (un emoji con tono de piel son
    /// cuatro unidades y un solo carácter visible). No usa <c>SnapshotText.Fit</c> porque ése sólo evita partir un par sustituto.
    /// </summary>
    private static string Fit(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var room = max - Ellipsis.Length;
        var length = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            if (length + element.Length > room)
            {
                break;
            }

            length += element.Length;
        }

        return string.Concat(text.AsSpan(0, length).TrimEnd(), Ellipsis);
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

    /// <summary>
    /// Ruling P2-R18: como lo escribe un cliente en Colombia. Puntos que separan grupos de exactamente tres dígitos son de miles
    /// («1.500», «1.500.000», «1.500,5») y la coma es decimal; si no, un solo punto o una sola coma es el decimal («1.5», «3.14»,
    /// «1234,5»). Sin exponente. Un primer grupo que empieza en 0 («0.500») no es de miles: nadie escribe así quinientos.
    /// </summary>
    private static decimal? Number(string text)
    {
        var normalized = ThousandsGrouped().IsMatch(text) ? text.Replace(".", string.Empty, StringComparison.Ordinal) : text;
        if (normalized.Contains('.', StringComparison.Ordinal) && normalized.Contains(',', StringComparison.Ordinal))
        {
            return null;
        }

        return decimal.TryParse(
            normalized.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
            out var number)
            ? number
            : null;
    }

    [GeneratedRegex(@"^[+-]?[1-9][0-9]{0,2}(\.[0-9]{3})+(,[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ThousandsGrouped();

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
    /// mensaje viejo) no vale aunque el título coincida. Sin id, el texto escrito se compara con las etiquetas (<see cref="Typed"/>).</summary>
    private static ReplyOutcome Choose(ButtonsNode node, EngineInput.CustomerMessage message)
    {
        if (Pressed(node.Buttons, message) is not { } button)
        {
            return ReplyOutcome.Invalid.Instance;
        }

        var saved = string.IsNullOrWhiteSpace(node.SaveTo) ? null : new JsonObject { ["id"] = button.Id, ["title"] = button.Label };
        return new ReplyOutcome.Output(button.Id, saved);
    }

    /// <summary>Spec 2026-10-26 D-1 y D-I5: los botones fijos de Política de datos, con la regla de Botones. No guarda nada: la respuesta
    /// la registra el motor como efecto, no una variable.</summary>
    private static ReplyOutcome Choose(IReadOnlyList<FlowButton> buttons, EngineInput.CustomerMessage message) =>
        Pressed(buttons, message) is { } button ? new ReplyOutcome.Output(button.Id, null) : ReplyOutcome.Invalid.Instance;

    private static FlowButton? Pressed(IReadOnlyList<FlowButton> buttons, EngineInput.CustomerMessage message) =>
        message.ReplyId is { } replyId
            ? buttons.FirstOrDefault(candidate => candidate.Id == replyId)
            : Typed(buttons, message, candidate => [candidate.Label]);

    /// <summary>Como en Botones (D-I5). La fila elegida guarda <c>{ id, title }</c>, y en una lista dinámica también el elemento entero de
    /// la colección (<c>item</c>), para que los bloques que siguen lean cualquier campo del equipo o la dirección elegida. La
    /// <c>extraOption</c> no guarda nada: no es un elemento.</summary>
    private static ReplyOutcome Choose(ListNode node, EngineInput.CustomerMessage message, RunVariables variables)
    {
        var rows = RowsOf(node, variables);
        var row = message.ReplyId is { } replyId
            ? rows.FirstOrDefault(candidate => candidate.Id == replyId)
            : Typed(rows, message, candidate => [candidate.FullTitle, candidate.Title]);

        if (row is null)
        {
            return ReplyOutcome.Invalid.Instance;
        }

        if (row.IsExtra)
        {
            return new ReplyOutcome.Output(FlowOutputs.Extra, null);
        }

        // El título completo: es el dato; el recorte (P2-R15) es sólo cómo cupo en WhatsApp.
        var saved = new JsonObject { ["id"] = row.Id, ["title"] = row.FullTitle };
        if (row.Item is not null)
        {
            saved["item"] = row.Item.DeepClone();
        }

        return new ReplyOutcome.Output(FlowOutputs.Chosen, saved);
    }

    /// <summary>
    /// Ruling P2-R19: la opción que el cliente escribió. Sólo texto o interactivo: el pie de una foto («Ventas») no elige un botón. Si el
    /// texto coincide con más de una opción (dos títulos iguales sin tildes ni mayúsculas) es ambiguo y no vale; tocar la opción sí.
    /// </summary>
    private static T? Typed<T>(IEnumerable<T> options, EngineInput.CustomerMessage message, Func<T, string[]> titles)
        where T : class
    {
        if (message.Kind is not (CustomerMessageKinds.Text or CustomerMessageKinds.Interactive) || string.IsNullOrWhiteSpace(message.Text))
        {
            return null;
        }

        var matches = options.Where(option => TextMatch.IsAnyOf(message.Text, titles(option))).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>§5.4: guarda los datos del adjunto, no el archivo. <c>url</c> sólo si el procesador la firmó (el archivo ya está en R2,
    /// D-M8 y D-H8); sin ella la variable no la lleva, y <c>{saveTo.url}</c> se interpola vacío.</summary>
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
        if (attachment.Url is { } url)
        {
            saved["url"] = url;
        }

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

/// <summary>Una fila de la Lista. <see cref="Title"/> y <see cref="Description"/> son los que se mandan, ya recortados a los topes de Meta;
/// <see cref="FullTitle"/> es el original, que el cliente también puede escribir. <see cref="Item"/> es el elemento de la colección en una
/// dinámica.</summary>
internal sealed record ListRow(string Id, string Title, string? Description, string FullTitle, JsonNode? Item, bool IsExtra);
