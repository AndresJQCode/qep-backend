using System.Text.Json;
using Modules.Automations.Application;
using Modules.Automations.Domain;
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
        // warnings, así que el conjunto se fija exacto: vacío. Las salidas opcionales (`invalid`, `other`, `tooMany`) van todas a
        // «Pasar a un asesor» (2026-10-23): sin conectar, agotar los intentos terminaba la corrida en silencio y el cliente se
        // quedaba sin respuesta hasta volver a escribir, donde el bot arrancaba de cero.
        Assert.Empty(issues);
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
            "GET /customers", "PUT /customers/{clientesPorTelefono[0].id}", "PUT /customers/{cliente.id}",
            "GET /invoices/customer/{cliente.id}/all-invoice-items",
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

    /// <summary>La política se acepta con dos botones (2026-10-23; antes era una pregunta abierta que pedía escribir «acepto»). Un
    /// toque se resuelve por id; lo escrito se compara contra la etiqueta sin tildes ni mayúsculas (§5.4), y cualquier otro texto
    /// repite la pregunta. «No acepto» termina la conversación con su propio mensaje (<c>policy-declined</c>). Aquí Clasic no
    /// conoce el teléfono (<c>clientesPorTelefono</c> vacía), así que «Acepto» sigue a la bienvenida sin guardar nada; con cliente
    /// por teléfono iría por <c>policy-save</c> (lo cubre <c>ClasicFlowEndToEndTests</c>). La bienvenida es un mensaje que no espera,
    /// así que la corrida queda esperando en <c>document</c>.</summary>
    [Theory]
    [InlineData("acepto", "Acepto", "document")]
    [InlineData("no-acepto", "No acepto", "policy-declined")]
    [InlineData(null, "acepto", "document")]
    [InlineData(null, "NO ACEPTO", "policy-declined")]
    [InlineData(null, "Acepto la política", "policy")]
    public void ThePolicyButtonsDecideWhereTheFlowGoes(string? replyId, string text, string expectedNode)
    {
        // El mismo camino que FlowEngineWaitingTests: el flujo arranca en la pregunta, espera, y la respuesta decide a dónde va.
        var flow = ClasicFlow.Parse() with { Start = new FlowStart("policy") };
        var settings = new EngineSettings("reiniciar", 3);
        var now = new DateTimeOffset(2026, 10, 22, 15, 0, 0, TimeSpan.Zero);
        var waiting = FlowEngine.Step(flow, settings, RunState.Initial(RunVariables.Empty()), new EngineInput.Start(), now);

        var after = FlowEngine.Step(
            flow, settings, waiting.State, new EngineInput.CustomerMessage(replyId is null ? "Text" : "Interactive", text, replyId, null), now);

        Assert.Equal(expectedNode, after.State.CurrentNodeId);
        Assert.Equal(expectedNode != "policy", after.State.Variables.Has("aceptacion"));
    }

    /// <summary>Spec 2026-10-25 D-2: los botones de la política, sin salida de agotamiento, aplican la regla del chatbot.</summary>
    [Fact]
    public void ThePolicyButtonsHandOffWhenAttemptsRunOutAndTheChatbotSaysSo()
    {
        var flow = ClasicFlow.Parse() with { Start = new FlowStart("policy") };
        var settings = new EngineSettings("reiniciar", 3, AttemptsExhaustedAction.Handoff);
        var now = new DateTimeOffset(2026, 10, 25, 15, 0, 0, TimeSpan.Zero);
        var step = FlowEngine.Step(flow, settings, RunState.Initial(RunVariables.Empty()), new EngineInput.Start(), now);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            step = FlowEngine.Step(flow, settings, step.State, new EngineInput.CustomerMessage("Text", "hola", null, null), now);
        }

        Assert.Equal((RunStatus.HandedOff, "policy"), (step.State.Status, step.State.CurrentNodeId));
        Assert.Equal<Effect>([new Effect.Handoff("policy", HandoffReasons.AttemptsExhausted, null)], step.Effects);
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

    /// <summary>El fixture es el archivo que se importa en producción tal cual (README, «Clasic: conexión, importación y corte»): la
    /// forma entera de <see cref="ChatbotExportDto"/>, no sólo <c>flow</c> y <c>layout</c>, con la conexión «API externa» del fixture
    /// declarada en <c>externalApis</c> para que quien importa sepa qué mapear.</summary>
    [Fact]
    public void TheFixtureIsAnImportableExportFile()
    {
        var export = JsonSerializer.Deserialize<ChatbotExportDto>(ClasicFlow.Raw(), JsonSerializerOptions.Web)!;

        Assert.Equal(ChatbotExportDto.CurrentSchema, export.Schema);
        Assert.Equal("Clasic", export.Name);
        Assert.Equal("NewConversation", export.Trigger);
        Assert.Equal("reiniciar", export.RestartKeyword);
        Assert.Equal(3, export.MaxAttempts);
        var api = Assert.Single(export.ExternalApis);
        Assert.Equal(ClasicFlow.ClasicApi, api.Id);
        Assert.Equal(ClasicFlow.ClasicApi, Assert.Single(HttpNodes(ClasicFlow.Parse()).Select(n => n.ConnectionId).Distinct()));
    }

    [Fact]
    public void EveryNodeHasALayoutPosition()
    {
        var positions = ClasicFlow.LayoutPositionIds().Order().ToList();

        Assert.Equal(ClasicFlow.Parse().Nodes.Select(n => n.Id).Order().ToList(), positions);
    }
}
