using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>El flujo completo de Clasic (spec 2026-10-21 Anexo B) tal como se importa al tenant de Clasic.</summary>
public sealed class ClasicFlowTests
{
    private static IEnumerable<HttpNode> HttpNodes(FlowDefinition flow) => flow.Nodes.OfType<HttpNode>();

    [Fact]
    public void TheFlowIsValidForAWhatsAppConnectionWithTheClasicApi()
    {
        var issues = FlowValidator.Validate(ClasicFlow.Parse(), ClasicFlow.Context());

        // Los defectos reales de un fixture de este tamaño (node.unreachable, variable.never_set, http.no_sample_response) salen como
        // warnings: el conjunto se fija exacto. Sólo quedan las salidas opcionales que el flujo deja sin conectar a propósito: el
        // `invalid` de una pregunta o un adjunto y el `other` de unos botones o una lista, que al agotar los intentos reinician el flujo.
        // `document` y `contact-phone` no están porque agotarlos pasa a un asesor (revisión final, 2026-10-23); `open-order-notice` sí,
        // porque ahora son botones y su `other` queda sin conectar como el de los demás.
        Assert.DoesNotContain(issues, i => i.Severity == FlowIssueSeverity.Error);
        string[] unconnected =
        [
            "policy", "not-found-options", "new-first-name", "new-last-name", "new-address", "new-neighborhood", "new-city",
            "new-failure", "invoice-photo", "equipment", "open-order-notice", "other-address", "service-kind", "failure", "contact-name",
        ];
        Assert.Equal(
            unconnected.Select(nodeId => ("node.output_unconnected_optional", nodeId)).Order(),
            issues.Select(i => (i.Code, i.NodeId!)).Order());
    }

    [Fact]
    public void EveryHttpBlockUsesTheClasicConnection()
    {
        var http = HttpNodes(ClasicFlow.Parse()).ToList();

        Assert.NotEmpty(http);
        Assert.All(http, node => Assert.Equal(ClasicFlow.ClasicApi, node.ConnectionId));
    }

    [Fact]
    public void EveryHttpPathIsInTheClasicContract()
    {
        string[] contract =
        [
            "GET /customers", "PUT /customers/{clientesPorTelefono[0].id}", "GET /invoices/customer/{cliente.id}/all-invoice-items",
            "GET /failures", "GET /services", "GET /service-orders/auto-select-service-type", "POST /service-orders",
            "POST /customers/new-customer",
        ];

        var used = HttpNodes(ClasicFlow.Parse()).Select(node => $"{node.Method} {node.Path}").ToHashSet();

        Assert.True(used.IsSubsetOf(contract), string.Join(", ", used.Except(contract)));
    }

    [Fact]
    public void CustomersAreReadFromItemsNotData()
    {
        var reads = HttpNodes(ClasicFlow.Parse()).Where(node => node.Method == "GET" && node.Path == "/customers").ToList();

        Assert.Equal(2, reads.Count);
        Assert.All(reads, node => Assert.All(node.Mapping, m => Assert.StartsWith("items", m.From, StringComparison.Ordinal)));
    }

    /// <summary>El cliente se busca con el filtro exacto <c>document</c>: <c>identificationNumber</c> es la búsqueda del admin y casa por
    /// subcadena, así que «1» traería el cliente, las direcciones y la orden de otra persona.</summary>
    [Fact]
    public void TheCustomerIsFoundByTheExactDocumentFilter()
    {
        var node = HttpNodes(ClasicFlow.Parse()).Single(n => n.Id == "find-customer");

        Assert.Equal([new KeyValue("document", "{cedula}")], node.Query);
    }

    [Fact]
    public void NewCustomerSendsEveryFieldTheDtoRequires()
    {
        var node = HttpNodes(ClasicFlow.Parse()).Single(n => n.Id == "new-customer");

        string[] required =
            ["identificationNumber", "firstName", "lastName", "address", "neighborhood", "phone", "departmentId", "cityId", "failureId"];
        Assert.NotNull(node.Body);
        var keys = node.Body!.Fields!.Select(field => field.Key).ToHashSet();
        Assert.True(keys.IsSupersetOf(required), string.Join(", ", required.Except(keys)));
        Assert.Equal("file", node.Body.FileField);
    }

    /// <summary>Como el bot viejo (<c>includes('acepto')</c>): cualquier texto con «acepto», sin importar mayúsculas, puntuación ni emoji,
    /// pero no si dice «no» (revisión final, 2026-10-23: con palabras clave «Sí, acepto.» no valía).</summary>
    [Theory]
    [InlineData("ACEPTO", true)]
    [InlineData("Acepto.", true)]
    [InlineData("sí, acepto", true)]
    [InlineData("Sí, acepto.", true)]
    [InlineData("Acepto!", true)]
    [InlineData("acepto 👍", true)]
    [InlineData("Acepto la política", true)]
    [InlineData("no acepto", false)]
    [InlineData("No acepto", false)]
    [InlineData("no", false)]
    public void ThePolicyQuestionAcceptsTheUsualWaysOfSayingYes(string reply, bool accepted)
    {
        // El mismo camino que FlowEngineWaitingTests: el flujo arranca en la pregunta, espera, y la respuesta decide a dónde va.
        var flow = ClasicFlow.Parse() with { Start = new FlowStart("policy") };
        var settings = new EngineSettings("reiniciar", 3);
        var now = new DateTimeOffset(2026, 10, 22, 15, 0, 0, TimeSpan.Zero);
        var waiting = FlowEngine.Step(flow, settings, RunState.Initial(RunVariables.Empty()), new EngineInput.Start(), now);

        var after = FlowEngine.Step(flow, settings, waiting.State, new EngineInput.CustomerMessage("Text", reply, null, null), now);

        // Aceptada, guarda la respuesta y sigue por policy-save (HTTP); rechazada, sigue esperando en la misma pregunta.
        Assert.Equal(accepted ? "policy-save" : "policy", after.State.CurrentNodeId);
        Assert.Equal(accepted, after.State.Variables.Has("aceptacion"));
    }

    /// <summary>Un documento que no son sólo dígitos («1.036.123.456») tres veces seguidas no reinicia el flujo desde la política: pasa a un
    /// asesor con su propio aviso (revisión final, 2026-10-23).</summary>
    [Fact]
    public void ExhaustingTheDocumentAttemptsHandsOff()
    {
        var flow = ClasicFlow.Parse() with { Start = new FlowStart("document") };
        var settings = new EngineSettings("reiniciar", 3);
        var now = new DateTimeOffset(2026, 10, 23, 15, 0, 0, TimeSpan.Zero);
        var step = FlowEngine.Step(flow, settings, RunState.Initial(RunVariables.Empty()), new EngineInput.Start(), now);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            step = FlowEngine.Step(flow, settings, step.State, new EngineInput.CustomerMessage("Text", "1.036.123.456", null, null), now);
        }

        Assert.Equal((RunStatus.HandedOff, "document-handoff"), (step.State.Status, step.State.CurrentNodeId));
        Assert.False(step.State.Variables.Has("cedula"));
    }

    [Theory]
    [InlineData("1036123456", true)]
    [InlineData("1234", true)]
    [InlineData("123", false)]
    [InlineData("1.036.123.456", false)]
    [InlineData("1036 123 456", false)]
    [InlineData("١٢٣٤٥", false)]
    public void TheDocumentQuestionAcceptsOnlyAsciiDigits(string reply, bool accepted)
    {
        var flow = ClasicFlow.Parse() with { Start = new FlowStart("document") };
        var settings = new EngineSettings("reiniciar", 3);
        var now = new DateTimeOffset(2026, 10, 23, 15, 0, 0, TimeSpan.Zero);
        var waiting = FlowEngine.Step(flow, settings, RunState.Initial(RunVariables.Empty()), new EngineInput.Start(), now);

        var after = FlowEngine.Step(flow, settings, waiting.State, new EngineInput.CustomerMessage("Text", reply, null, null), now);

        // Aceptado, sigue a la búsqueda por documento (HTTP); rechazado, sigue esperando en la misma pregunta.
        Assert.Equal(accepted ? "find-customer" : "document", after.State.CurrentNodeId);
    }

    [Fact]
    public void EveryNodeHasALayoutPosition()
    {
        var positions = ClasicFlow.LayoutPositionIds().Order().ToList();

        Assert.Equal(ClasicFlow.Parse().Nodes.Select(n => n.Id).Order().ToList(), positions);
    }
}
