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

        var errors = issues.Where(i => i.Severity == FlowIssueSeverity.Error).Select(i => $"{i.Code}@{i.NodeId}").ToList();
        Assert.True(errors.Count == 0, string.Join(", ", errors));
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

    [Theory]
    [InlineData("ACEPTO", true)]
    [InlineData("Acepto.", true)]
    [InlineData("sí, acepto", true)]
    [InlineData("no", false)]
    public void ThePolicyQuestionAcceptsTheUsualWaysOfSayingYes(string reply, bool accepted)
    {
        // El mismo camino que FlowEngineWaitingTests: el flujo arranca en la pregunta, espera, y la respuesta decide a dónde va.
        var flow = ClasicFlow.Parse() with { Start = new FlowStart("policy") };
        var settings = new EngineSettings("reiniciar", 3);
        var now = new DateTimeOffset(2026, 10, 22, 15, 0, 0, TimeSpan.Zero);
        var waiting = FlowEngine.Step(flow, settings, RunState.Initial(RunVariables.Empty()), new EngineInput.Start(), now);

        var after = FlowEngine.Step(flow, settings, waiting.State, new EngineInput.CustomerMessage("Text", reply, null, null), now);

        // Aceptada, sigue por policy-save (HTTP) y se detiene ahí; rechazada, sigue esperando en la misma pregunta.
        Assert.Equal(accepted, after.State.CurrentNodeId != "policy");
    }

    [Fact]
    public void EveryNodeHasALayoutPosition()
    {
        var positions = ClasicFlow.LayoutPositionIds().Order().ToList();

        Assert.Equal(ClasicFlow.Parse().Nodes.Select(n => n.Id).Order().ToList(), positions);
    }
}
