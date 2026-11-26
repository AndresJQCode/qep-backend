using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.UnitTests;

/// <summary>
/// Spec 2026-10-29 §8.2, P-4 y P-4b: <c>create_quotation_draft</c> en un canal real. El cliente sale de la conversación, la asesora y la
/// cuenta de cobro del agente; la clave externa es la del trabajo y la llamada (D-A16). Creado el borrador, o sin cliente que se pueda
/// cotizar, el agente traspasa: a la asesora de la cotización en el primer caso, al destino configurado en el segundo.
/// </summary>
public sealed class CreateQuotationDraftToolTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid JobId = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid Advisor = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    private static readonly Guid Customer = Guid.Parse("01900000-0000-7000-8000-0000000000c2");
    private static readonly Guid VariantA = Guid.Parse("01900000-0000-7000-8000-0000000000d1");
    private static readonly Guid VariantB = Guid.Parse("01900000-0000-7000-8000-0000000000d2");
    private static readonly AgentBillingAccount Account = new(Guid.Parse("01900000-0000-7000-8000-0000000000e1"), "Bancolombia", "123-456", "COP");

    private readonly FakeDrafts _drafts = new();
    private readonly FakeAdvisors _advisors = new();
    private readonly ScriptedModel _model = new();
    private readonly List<AiToolCallRecord> _calls = [];

    [Fact]
    public async Task TheDraftIsForTheConversationsCustomerWithTheAgentsAdvisorAndAccountAndTheJobsKey()
    {
        _model.Enqueue(Call("call_q", new
        {
            lines = new[] { new { variantId = VariantA, quantity = 2 }, new { variantId = VariantB, quantity = 1 } },
            note = "Quiere entrega el lunes.",
            customerId = Guid.NewGuid(),
        }));

        await RunAsync(Context());

        var request = Assert.Single(_drafts.Requests);
        Assert.Equal((Tenant, Customer, Advisor, Account), (request.TenantId, request.CustomerId, request.AdvisorMemberId, request.BillingAccount));
        Assert.Equal([new AiQuotationDraftLine(VariantA, 2), new AiQuotationDraftLine(VariantB, 1)], request.Lines);
        Assert.Equal("Quiere entrega el lunes.", request.Note);
        Assert.Equal($"ai:{JobId}", request.ExternalKey);
    }

    // Revisión final, hallazgo 1: el id de la llamada lo inventa el modelo y cambia si el trabajo se retoma y el modelo corre otra vez. La
    // clave es del trabajo, así el segundo intento encuentra el borrador del primero en vez de crear otro.
    [Fact]
    public async Task ARerunOfTheSameJobAsksForTheSameDraftKey()
    {
        _model.Enqueue(Call("call_first_run", Lines()));
        _model.Enqueue(Call("call_second_run", Lines()));

        await RunAsync(Context());
        await RunAsync(Context());

        Assert.Equal([$"ai:{JobId}", $"ai:{JobId}"], _drafts.Requests.Select(request => request.ExternalKey));
    }

    // Revisión final, hallazgo 1: dos create_quotation_draft en la misma respuesta (llamadas en paralelo) crean un solo borrador; la segunda
    // vuelve con error y queda anotada.
    [Fact]
    public async Task TwoDraftCallsInOneResponseCreateOneDraft()
    {
        _model.Enqueue(new AiModelResponse(
            null,
            [
                new AiToolCall("call_a", AgentToolNames.CreateQuotationDraft, JsonSerializer.Serialize(Lines())),
                new AiToolCall("call_b", AgentToolNames.CreateQuotationDraft, JsonSerializer.Serialize(Lines())),
            ],
            [new AiOpaqueBlock("opaque")], AiStopReason.ToolUse, new AiModelUsage(100, 20, null, null), "claude-opus-5-5", "req_1"));

        var outcome = await RunAsync(Context());

        Assert.Single(_drafts.Requests);
        Assert.Equal(new AgentHandedOff(AgentHandoffCodes.QuotationDraftCreated, null, Advisor, "COT-0001", Wrote: true), outcome);
        Assert.Equal(
            [("call_a", AiRunOutcomes.Ok, (string?)null), ("call_b", AiRunOutcomes.Error, AgentToolErrors.OneWritePerResponse)],
            _calls.Select(call => (call.CallId, call.Outcome, call.ErrorCode)));
    }

    // P-4b: creado el borrador, el agente pasa la conversación a la asesora de la cotización para que la revise y la envíe.
    [Fact]
    public async Task AfterTheDraftTheAgentHandsOffToTheAdvisorOfTheQuotation()
    {
        var other = Guid.NewGuid();
        _drafts.Next = AiQuotationDraftResult.Created("COT-0042", other);
        _model.Enqueue(Call("call_q", Lines()));

        var outcome = await RunAsync(Context());

        // Spec 2026-11-17 D-B13: el número viaja con el traspaso, para el bloque del chatbot.
        Assert.Equal(new AgentHandedOff(AgentHandoffCodes.QuotationDraftCreated, null, other, "COT-0042", Wrote: true), outcome);
        Assert.Single(_model.Requests);
        var recorded = Assert.Single(_calls);
        Assert.Equal(AiRunOutcomes.Ok, recorded.Outcome);
        Assert.Contains("COT-0042", recorded.ResultJson, StringComparison.Ordinal);
    }

    // P-4: sin cliente en la conversación no se crea nada y el agente traspasa al destino configurado.
    [Fact]
    public async Task WithoutACustomerNothingIsCreatedAndTheAgentHandsOff()
    {
        _model.Enqueue(Call("call_q", Lines()));

        var outcome = await RunAsync(Context() with { CustomerId = null });

        Assert.Empty(_drafts.Requests);
        Assert.Equal(new AgentHandedOff(AgentHandoffCodes.QuotationCustomerNotReady, null), outcome);
    }

    // P-4: un cliente incompleto (o sin CUC, o inactivo) tampoco se cotiza: traspaso sin borrador.
    [Fact]
    public async Task ACustomerQuotationsRejectsHandsOffWithoutADraft()
    {
        _drafts.Next = AiQuotationDraftResult.CustomerNotReady("quotation.quotation.client_incomplete");
        _model.Enqueue(Call("call_q", Lines()));

        var outcome = await RunAsync(Context());

        Assert.Equal(new AgentHandedOff(AgentHandoffCodes.QuotationCustomerNotReady, null), outcome);
        Assert.Equal("quotation.quotation.client_incomplete", Assert.Single(_calls).ErrorCode);
    }

    // P-4: una asesora que ya no puede cotizar bloquea la herramienta; el modelo lo lee y sigue (puede traspasar él).
    [Fact]
    public async Task AnAdvisorWhoCanNoLongerQuoteBlocksTheTool()
    {
        _advisors.CanQuote = false;
        _model.Enqueue(Call("call_q", Lines()));
        _model.Enqueue(Final("Te comunico con un asesor."));

        var outcome = await RunAsync(Context());

        Assert.Empty(_drafts.Requests);
        Assert.IsType<AgentReplied>(outcome);
        var result = Assert.Single(Assert.Single(_model.Requests[1].PriorAssistantBlocks).ToolResults);
        Assert.True(result.IsError);
        Assert.Contains(AgentToolErrors.QuotationAdvisorUnavailable, result.ContentJson, StringComparison.Ordinal);
    }

    // Una variante sin precio o inexistente vuelve al modelo con el código de Quotations, para que corrija.
    [Fact]
    public async Task ADraftQuotationsRejectsGoesBackToTheModelWithItsCode()
    {
        _drafts.Next = AiQuotationDraftResult.Rejected("quotation.item.product_not_found");
        _model.Enqueue(Call("call_q", Lines()));
        _model.Enqueue(Final("No encontré ese producto."));

        var outcome = await RunAsync(Context());

        Assert.IsType<AgentReplied>(outcome);
        var result = Assert.Single(Assert.Single(_model.Requests[1].PriorAssistantBlocks).ToolResults);
        Assert.True(result.IsError);
        Assert.Contains("quotation.item.product_not_found", result.ContentJson, StringComparison.Ordinal);
    }

    public static TheoryData<string> InvalidArguments() =>
    [
        """{"lines":[],"note":null}""",
        """{"lines":[{"variantId":"01900000-0000-7000-8000-0000000000d1","quantity":0}],"note":null}""",
        """{"lines":[{"variantId":"01900000-0000-7000-8000-0000000000d1","quantity":10000}],"note":null}""",
        """{"lines":[{"variantId":"no-es-un-id","quantity":1}],"note":null}""",
        """{"lines":[{"variantId":"01900000-0000-7000-8000-0000000000d1","quantity":1},{"variantId":"01900000-0000-7000-8000-0000000000d1","quantity":2}],"note":null}""",
    ];

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task InvalidArgumentsNeverReachQuotations(string arguments)
    {
        _model.Enqueue(new AiModelResponse(
            null, [new AiToolCall("call_q", AgentToolNames.CreateQuotationDraft, arguments)], [new AiOpaqueBlock("opaque")], AiStopReason.ToolUse,
            new AiModelUsage(10, 10, null, null), "claude-opus-5-5", "req"));
        _model.Enqueue(Final("Revisa los productos."));

        await RunAsync(Context());

        Assert.Empty(_drafts.Requests);
        Assert.Equal(AgentToolErrors.InvalidArguments, Assert.Single(_calls).ErrorCode);
    }

    // §7: en el playground sólo se ofrece la simulada; en un canal real, sólo la real.
    [Fact]
    public void TheRealToolIsOnlyOfferedOnRealChannels()
    {
        var tools = new AgentTools(Catalog: false, OrderStatus: false, QuotationDraft: true);
        var real = new CreateQuotationDraftTool(_drafts, _advisors);
        var simulated = new SimulatedQuotationDraftTool();

        Assert.True(real.IsOfferedTo(tools, isTest: false));
        Assert.False(real.IsOfferedTo(tools, isTest: true));
        Assert.False(simulated.IsOfferedTo(tools, isTest: false));
        Assert.False(real.IsOfferedTo(tools with { QuotationDraft = false }, isTest: false));
        Assert.Equal(real.Definition, simulated.Definition);
    }

    private static object Lines() => new { lines = new[] { new { variantId = VariantA, quantity = 3 } }, note = (string?)null };

    private async Task<AgentTurnOutcome> RunAsync(AgentTurnContext context) =>
        await new AgentTurnRunner(
                new SingleResolver(_model),
                [new CreateQuotationDraftTool(_drafts, _advisors), new SimulatedQuotationDraftTool()],
                new Ledger(_calls),
                new NoCredentials(),
                new AiRunLimits(16_000, TimeSpan.FromSeconds(120), 6, 8, 30, null),
                new FixedClock(),
                NullLogger<AgentTurnRunner>.Instance)
            .RunAsync(context, new AgentPrompt("sistema", null, "ahora", [new AiChatMessage(AiChatRole.User, "cotízame")]), TestContext.Current.CancellationToken);

    private static AgentTurnContext Context()
    {
        var config = AgentConfig.Default(new AgentModelRef("anthropic", "claude-opus-5-5")) with
        {
            Instructions = "Cotiza.",
            Tools = new AgentTools(Catalog: false, OrderStatus: false, QuotationDraft: true),
            QuotationDraft = new AgentQuotationDraft(Advisor, Account),
        };
        return new AgentTurnContext(
            Tenant, Guid.NewGuid(), Guid.NewGuid(), AiProviders.Anthropic, "claude-opus-5-5", "medium", config,
            new AiCredential(Guid.NewGuid(), "sk-ant-api03-TEST"), JobId, Guid.NewGuid(), Guid.NewGuid(), null, Customer, [], IsTest: false);
    }

    private static AiModelResponse Call(string id, object arguments) =>
        new(
            null, [new AiToolCall(id, AgentToolNames.CreateQuotationDraft, JsonSerializer.Serialize(arguments))], [new AiOpaqueBlock("opaque")],
            AiStopReason.ToolUse, new AiModelUsage(100, 20, null, null), "claude-opus-5-5", "req_1");

    private static AiModelResponse Final(string text) =>
        new(
            text, [new AiToolCall("final", AgentToolNames.SendReply, JsonSerializer.Serialize(new { text, sourceIds = Array.Empty<string>() }))], [],
            AiStopReason.ToolUse, new AiModelUsage(100, 20, null, null), "claude-opus-5-5", "req_2");

    private sealed class FakeDrafts : IAiQuotationDrafts
    {
        public List<AiQuotationDraftRequest> Requests { get; } = [];

        public AiQuotationDraftResult Next { get; set; } = AiQuotationDraftResult.Created("COT-0001", Advisor);

        public Task<AiQuotationDraftResult> CreateAsync(AiQuotationDraftRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Next);
        }
    }

    private sealed class FakeAdvisors : IAiQuotationAdvisors
    {
        public bool CanQuote { get; set; } = true;

        public Task<IReadOnlyList<AiMemberRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiMemberRef>>([new AiMemberRef(Advisor, "Ana")]);

        public Task<bool> CanQuoteAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken) => Task.FromResult(CanQuote);
    }

    private sealed class ScriptedModel : IAiModelClient
    {
        private readonly Queue<AiModelResponse> _script = new();

        public List<AiModelRequest> Requests { get; } = [];

        public void Enqueue(AiModelResponse response) => _script.Enqueue(response);

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_script.Dequeue());
        }
    }

    private sealed class SingleResolver(IAiModelClient client) : IAiModelClientResolver
    {
        public IAiModelClient For(AiProvider provider) => client;
    }

    private sealed class Ledger(List<AiToolCallRecord> calls) : IAiRunLedger
    {
        public Task RecordUsageAsync(AiUsageRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordToolCallAsync(AiToolCallRecord record, CancellationToken cancellationToken)
        {
            calls.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class NoCredentials : IAiProviderCredentials
    {
        public Task<AiCredentialResolution> ResolveAsync(Guid tenantId, AiProvider provider, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AiProviderConnectionState>> DescribeAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkNeedsAttentionAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 11, 15, 15, 0, 0, TimeSpan.Zero);
    }
}
