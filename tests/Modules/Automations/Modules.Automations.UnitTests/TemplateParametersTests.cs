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
              "query": [ { "key": "q", "value": "{param.option1}" } ], "body": null, "timeoutSeconds": 8, "okWhen": null,
              "mapping": [ { "from": "{param.option1}", "to": "{param.option1}" } ], "sampleResponse": null,
              "outputs": { "ok": "n6", "error": "n7" } },
            { "id": "n6", "kind": "handoff", "title": "Asesor", "text": "Te paso con {param.option1}." },
            { "id": "n7", "kind": "resolve", "title": "Fin", "text": "Chao de {param.missing}.", "reasonId": null }
          ]
        }
        """;

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

        Assert.Equal("Te paso con Ventas.", Assert.IsType<HandoffNode>(flow.Nodes[5]).Text);
        // Sin valor para «missing», queda literal: Validar lo muestra y el admin lo corrige.
        Assert.Equal("Chao de {param.missing}.", Assert.IsType<ResolveNode>(flow.Nodes[6]).Text);
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
