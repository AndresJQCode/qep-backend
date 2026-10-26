using Modules.Automations.Application;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-10-21 D-C14: <c>{param.&lt;key&gt;}</c> se sustituye al instalar sólo en los campos de texto del flujo.</summary>
public sealed class TemplateParametersTests
{
    private static readonly Dictionary<string, string> Values = new(StringComparer.Ordinal)
    {
        ["greeting"] = "Hola, soy Clasic",
        ["option1"] = "Ventas",
        ["site"] = "https://clasic.co",
    };

    // Todo campo de texto lleva un parámetro, y también los que no son texto (ids, saveTo, variables, ruta, query, cuerpo): los
    // primeros cambian y los segundos quedan literales.
    private const string Flow = """
        {
          "schema": 1,
          "start": { "firstNodeId": "n1" },
          "variables": [ { "name": "{param.greeting}", "type": "text" } ],
          "nodes": [
            { "id": "n1", "kind": "buttons", "title": "Menú {param.option1}", "text": "{param.greeting}, ¿qué necesitas?",
              "buttons": [ { "id": "{param.option1}", "label": "{param.option1}" } ], "saveTo": "{param.option1}", "fallback": "repeat",
              "outputs": { "{param.option1}": "n2", "other": null } },
            { "id": "n2", "kind": "message", "title": "Enlace", "text": "Mira {param.site}",
              "urlButton": { "label": "Ir a {param.option1}", "url": "{param.site}/ayuda" }, "outputs": { "next": "n3" } },
            { "id": "n3", "kind": "openQuestion", "title": "Pregunta", "text": "¿Sí?", "saveTo": "{param.greeting}",
              "validation": { "kind": "keywords", "pattern": null, "keywords": [ "{param.option1}", "no" ] }, "maxAttempts": null,
              "outputs": { "answered": "n4", "invalid": null } },
            { "id": "n4", "kind": "list", "title": "Lista", "text": "Elige", "buttonLabel": "Ver {param.option1}",
              "options": { "kind": "fixed", "fixed": [ { "id": "a", "title": "{param.option1}", "description": "Sobre {param.site}" } ],
                "collection": null, "idField": null, "titleField": null, "descriptionField": null,
                "extraOption": { "id": "other-row", "title": "Otra {param.option1}", "description": null } },
              "saveTo": "eleccion", "fallback": "repeat", "outputs": { "chosen": "n5", "extra": "n6", "other": null } },
            { "id": "n5", "kind": "http", "title": "Buscar", "connectionId": null, "method": "GET", "path": "/{param.option1}",
              "query": [ { "key": "q", "value": "{param.option1}" } ],
              "body": { "kind": "json", "template": "{ \"q\": \"{param.option1}\" }", "fields": null, "fileField": null, "fileVariable": null },
              "timeoutSeconds": 8, "okWhen": null,
              "mapping": [ { "from": "{param.option1}", "to": "{param.option1}" } ], "sampleResponse": null,
              "outputs": { "ok": "n6", "error": "n7" } },
            { "id": "n6", "kind": "handoff", "title": "Asesor", "text": "Te paso con {param.option1}." },
            { "id": "n8", "kind": "attachment", "title": "Foto {param.option1}", "text": "Envía la foto de {param.option1}", "accept": "image",
              "saveTo": "{param.option1}", "outputs": { "received": "n9", "invalid": null } },
            { "id": "n9", "kind": "condition", "title": "¿Es {param.option1}?", "match": "all",
              "rules": [ { "variable": "{param.option1}", "operator": "equals", "value": "{param.option1}" } ],
              "outputs": { "yes": "n6", "no": "n7" } },
            { "id": "n7", "kind": "resolve", "title": "Fin", "text": "Chao de {param.missing}.", "reasonId": null }
          ]
        }
        """;

    [Fact]
    public void ADataConsentTextAndTitleTakeTheParameters()
    {
        const string json = """
            {
              "schema": 1,
              "start": { "firstNodeId": "d1" },
              "variables": [],
              "nodes": [
                { "id": "d1", "kind": "dataConsent", "title": "Política {param.politica}", "text": "Lee {param.politica}",
                  "outputs": { "accepted": "d1", "rejected": null } }
              ]
            }
            """;

        var flow = FlowJson.Parse(TemplateParameters.Apply(json, new Dictionary<string, string>(StringComparer.Ordinal) { ["politica"] = "https://empresa.co/datos" }));

        var consent = Assert.IsType<DataConsentNode>(Assert.Single(flow.Nodes));
        Assert.Equal("Política https://empresa.co/datos", consent.Title);
        Assert.Equal("Lee https://empresa.co/datos", consent.Text);
        Assert.Equal(["accepted", "rejected"], consent.Outputs.Keys);
    }

    [Fact]
    public void ApplyReplacesParamsOnlyInTextFields()
    {
        var flow = FlowJson.Parse(TemplateParameters.Apply(Flow, Values));

        Assert.Equal("{param.greeting}", Assert.Single(flow.Variables).Name);
        var buttons = Assert.IsType<ButtonsNode>(flow.Nodes[0]);
        Assert.Equal(("Menú Ventas", "Hola, soy Clasic, ¿qué necesitas?"), (buttons.Title, buttons.Text));
        Assert.Equal(new FlowButton("{param.option1}", "Ventas"), Assert.Single(buttons.Buttons));
        Assert.Equal("{param.option1}", buttons.SaveTo);
        Assert.Equal("n2", buttons.Outputs["{param.option1}"]);

        var message = Assert.IsType<MessageNode>(flow.Nodes[1]);
        Assert.Equal("Mira https://clasic.co", message.Text);
        Assert.Equal(new UrlButton("Ir a Ventas", "https://clasic.co/ayuda"), message.UrlButton);

        var question = Assert.IsType<OpenQuestionNode>(flow.Nodes[2]);
        Assert.Equal("{param.greeting}", question.SaveTo);
        Assert.Equal(["Ventas", "no"], question.Validation.Keywords!);

        var list = Assert.IsType<ListNode>(flow.Nodes[3]);
        Assert.Equal("Ver Ventas", list.ButtonLabel);
        Assert.Equal(new ListFixedRow("a", "Ventas", "Sobre https://clasic.co"), Assert.Single(list.Options.Fixed!));
        Assert.Equal(new ListFixedRow("other-row", "Otra Ventas", null), list.Options.ExtraOption);

        var http = Assert.IsType<HttpNode>(flow.Nodes[4]);
        Assert.Equal("/{param.option1}", http.Path);
        Assert.Equal("{param.option1}", Assert.Single(http.Query).Value);
        Assert.Equal(new HttpMapping("{param.option1}", "{param.option1}"), Assert.Single(http.Mapping));
        // El cuerpo es JSON con variables, no texto que el cliente lea.
        Assert.Equal("{ \"q\": \"{param.option1}\" }", http.Body!.Template);

        Assert.Equal("Te paso con Ventas.", Assert.IsType<HandoffNode>(flow.Nodes[5]).Text);

        var attachment = Assert.IsType<AttachmentNode>(flow.Nodes[6]);
        Assert.Equal(("Foto Ventas", "Envía la foto de Ventas", "{param.option1}"), (attachment.Title, attachment.Text, attachment.SaveTo));

        // La regla compara contra un valor, no le habla al cliente: variable y valor quedan literales; sólo el título cambia.
        var condition = Assert.IsType<ConditionNode>(flow.Nodes[7]);
        Assert.Equal("¿Es Ventas?", condition.Title);
        Assert.Equal(new ConditionRule("{param.option1}", "equals", "{param.option1}"), Assert.Single(condition.Rules));

        // Sin valor para «missing», queda literal: Validar lo muestra y el admin lo corrige.
        Assert.Equal("Chao de {param.missing}.", Assert.IsType<ResolveNode>(flow.Nodes[8]).Text);
    }

    [Fact]
    public void ResolveUsesTheDefaultAndIgnoresUndeclaredKeys()
    {
        TemplateParameter[] parameters =
        [
            new("greeting", "Saludo", "text", null, "¡Hola!"),
            new("option1", "Primera opción", "text", null, "Ventas"),
        ];

        var resolved = TemplateParameters.Resolve(
            parameters, new Dictionary<string, string> { ["greeting"] = "Hola, soy Clasic", ["intruso"] = "x" });

        Assert.Equal(
            [("greeting", "Hola, soy Clasic"), ("option1", "Ventas")],
            resolved.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)));
    }

    [Fact]
    public void ApplyKeepsAFlowWithoutParamsAsTheSerializedModel()
    {
        var original = FlowJson.Serialize(AnnexAFlow.Parse());

        Assert.Equal(original, TemplateParameters.Apply(AnnexAFlow.Json, Values));
    }
}
