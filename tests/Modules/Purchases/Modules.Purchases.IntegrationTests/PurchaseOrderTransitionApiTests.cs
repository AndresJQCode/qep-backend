using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Purchases.Application;
using static Modules.Purchases.IntegrationTests.PurchasesApiHarness;

namespace Modules.Purchases.IntegrationTests;

/// <summary>El ciclo de la orden por HTTP: enviar, confirmar (con o sin cambios), recibir y cancelar.</summary>
public sealed class PurchaseOrderTransitionApiTests(SharedPurchasesHost host) : IClassFixture<SharedPurchasesHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private QepApiFactory Factory => host.Factory;

    private static Task<PurchaseOrderDetailDto> CreateDraftAsync(PurchasesScenario scenario) =>
        CreateOrderAsync(
            scenario.Manager,
            scenario.TenantId,
            OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m), (scenario.VariantB, 3m, 50m), (scenario.VariantC, 1m, 10m)));

    private static string ActionUrl(PurchasesScenario scenario, PurchaseOrderDetailDto order, string action) =>
        $"{OrderUrl(scenario.TenantId, order.Id)}/{action}";

    [Fact]
    public async Task SendConfirmReceiveWalksTheHappyPathAndTheHistory()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var draft = await CreateDraftAsync(scenario);

        var sent = await TransitionAsync(scenario.Manager, scenario.TenantId, draft, "send");
        var confirmed = await ConfirmAsIsAsync(scenario.Manager, scenario.TenantId, sent);
        var received = await TransitionAsync(scenario.Manager, scenario.TenantId, confirmed, "receive");

        Assert.Equal(("Sent", "Confirmed", "Received"), (sent.Status, confirmed.Status, received.Status));
        Assert.Equal(["Draft", "Sent", "Confirmed", "Received"], received.History.Select(entry => entry.Status).ToArray());
        Assert.All(received.History, entry => Assert.Equal(MemberName, entry.ActorName));
        // Una orden recibida ya no transiciona, pero sigue pudiendo reenviar su correo al proveedor.
        Assert.Equal(["resend_email"], received.AllowedActions);
        Assert.False(received.ConfirmedWithChanges);
    }

    [Fact]
    public async Task ConfirmWithChangesMarksLinesAndUsesConfirmedTotals()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var sent = await TransitionAsync(scenario.Manager, scenario.TenantId, await CreateDraftAsync(scenario), "send");
        var (a, b, c) = (sent.Lines[0], sent.Lines[1], sent.Lines[2]);

        var response = await WriteAsync(
            scenario.Manager,
            HttpMethod.Post,
            ActionUrl(scenario, sent, "confirm"),
            sent.Version,
            ConfirmBody((a.Id, 2m, 100m), (b.Id, 3m, 60m), (c.Id, 0m, 10m)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var confirmed = (await response.Content.ReadFromJsonAsync<PurchaseOrderDetailDto>(Ct))!;
        Assert.Equal(["Unchanged", "Changed", "Rejected"], confirmed.Lines.Select(line => line.LineStatus).ToArray());
        Assert.True(confirmed.ConfirmedWithChanges);
        // A: 200 + 38; B: 3 × 60 = 180 + 34.20; C rechazada: 0.
        Assert.Equal(new PurchaseOrderTotalsDto(380m, 72.2m, 452.2m), confirmed.Totals);
        // Lo pedido: 238 + 178.50 + 10.
        Assert.Equal(426.5m, confirmed.RequestedTotal);
    }

    // Review Focus 4: el cuerpo trae cada línea exactamente una vez, o no se toca nada.
    [Theory]
    [InlineData("missing")]
    [InlineData("repeated")]
    [InlineData("foreign")]
    public async Task ConfirmWithMismatchedLinesIs422AndChangesNothing(string mismatch)
    {
        using var scenario = await NewScenarioAsync(Factory);
        var sent = await TransitionAsync(scenario.Manager, scenario.TenantId, await CreateDraftAsync(scenario), "send");
        var lines = sent.Lines.Select(line => (line.Id, line.Quantity!.Value, line.UnitCost!.Value)).ToList();
        switch (mismatch)
        {
            case "missing":
                lines.RemoveAt(2);
                break;
            case "repeated":
                lines[2] = lines[0];
                break;
            default:
                lines[2] = (Guid.CreateVersion7(), 1m, 10m);
                break;
        }

        var response = await WriteAsync(
            scenario.Manager, HttpMethod.Post, ActionUrl(scenario, sent, "confirm"), sent.Version, ConfirmBody([.. lines]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("purchases.order.confirm_lines_mismatch", await CodeOfAsync(response));
        var read = await GetOrderAsync(scenario.Manager, scenario.TenantId, sent.Id);
        Assert.Equal(("Sent", sent.Version), (read.Status, read.Version));
    }

    // Review Focus 5: enviar vuelve a mirar el proveedor y el borrador sigue cancelable.
    [Fact]
    public async Task SendingToADeactivatedSupplierIs422AndLeavesTheDraft()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var draft = await CreateDraftAsync(scenario);
        await DeactivateSupplierAsync(scenario.Manager, scenario.TenantId, scenario.SupplierId);

        var send = await WriteAsync(scenario.Manager, HttpMethod.Post, ActionUrl(scenario, draft, "send"), draft.Version);
        var read = await GetOrderAsync(scenario.Manager, scenario.TenantId, draft.Id);
        var cancelled = await TransitionAsync(scenario.Manager, scenario.TenantId, read, "cancel");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, send.StatusCode);
        Assert.Equal("purchases.order.supplier_inactive", await CodeOfAsync(send));
        Assert.Equal(("Draft", draft.Version), (read.Status, read.Version));
        Assert.Equal("Cancelled", cancelled.Status);
    }

    [Fact]
    public async Task InvalidTransitionsAre409()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var draft = await CreateDraftAsync(scenario);
        var receiveDraft = await WriteAsync(
            scenario.Manager, HttpMethod.Post, ActionUrl(scenario, draft, "receive"), draft.Version);
        var sent = await TransitionAsync(scenario.Manager, scenario.TenantId, draft, "send");
        var sendSent = await WriteAsync(scenario.Manager, HttpMethod.Post, ActionUrl(scenario, sent, "send"), sent.Version);
        var received = await TransitionAsync(
            scenario.Manager, scenario.TenantId, await ConfirmAsIsAsync(scenario.Manager, scenario.TenantId, sent), "receive");
        var cancelReceived = await WriteAsync(
            scenario.Manager, HttpMethod.Post, ActionUrl(scenario, received, "cancel"), received.Version);

        foreach (var response in new[] { receiveDraft, sendSent, cancelReceived })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("purchases.order.invalid_transition", await CodeOfAsync(response));
        }
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Sent")]
    [InlineData("Confirmed")]
    public async Task CancelFromDraftSentAndConfirmedSucceeds(string from)
    {
        using var scenario = await NewScenarioAsync(Factory);
        var order = await CreateDraftAsync(scenario);
        if (from is "Sent" or "Confirmed")
        {
            order = await TransitionAsync(scenario.Manager, scenario.TenantId, order, "send");
        }

        if (from == "Confirmed")
        {
            order = await ConfirmAsIsAsync(scenario.Manager, scenario.TenantId, order);
        }

        var cancelled = await TransitionAsync(scenario.Manager, scenario.TenantId, order, "cancel");

        Assert.Equal(from, order.Status);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal("Cancelled", cancelled.History[^1].Status);
        Assert.Empty(cancelled.AllowedActions);
    }

    private FakePurchaseOrderEmail Emails => Factory.Services.GetRequiredService<FakePurchaseOrderEmail>();

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task SendEmailsTheSupplierWithThePdfAndReturnsEmailSent()
    {
        var address = FakePurchaseOrderEmail.NewAddress();
        using var scenario = await NewScenarioAsync(Factory, address);

        var sent = await TransitionAsync(scenario.Manager, scenario.TenantId, await CreateDraftAsync(scenario), "send");

        var request = Assert.Single(Emails.SentTo(address));
        Assert.Equal(FakePurchaseOrderPdfRenderer.Content, request.Pdf);
        Assert.Equal(("Sent", address, address), (sent.Email!.Status, sent.Email.To, sent.Email.Recipient));
        Assert.Contains("resend_email", sent.AllowedActions);
        // El resultado queda guardado: una lectura posterior lo trae.
        var reread = await scenario.Manager.GetFromJsonAsync<PurchaseOrderDetailDto>(OrderUrl(scenario.TenantId, sent.Id), Ct);
        Assert.Equal("Sent", reread!.Email!.Status);
    }

    [Fact]
    public async Task SendWithoutASupplierEmailIs200WithNoRecipientAndSendsNothing()
    {
        using var scenario = await NewScenarioAsync(Factory);

        var sent = await TransitionAsync(scenario.Manager, scenario.TenantId, await CreateDraftAsync(scenario), "send");

        Assert.Equal("Sent", sent.Status);
        Assert.Equal("NoRecipient", sent.Email!.Status);
        Assert.Null(sent.Email.Recipient);
    }

    [Fact]
    public async Task SendWhenTheEmailFailsStillSendsTheOrder()
    {
        var address = FakePurchaseOrderEmail.NewAddress(failing: true);
        using var scenario = await NewScenarioAsync(Factory, address);

        var sent = await TransitionAsync(scenario.Manager, scenario.TenantId, await CreateDraftAsync(scenario), "send");

        Assert.Equal(("Sent", "Failed"), (sent.Status, sent.Email!.Status));
    }

    [Fact]
    public async Task ResendEmailIs200AndKeepsTheVersion()
    {
        var address = FakePurchaseOrderEmail.NewAddress();
        using var scenario = await NewScenarioAsync(Factory, address);
        var sent = await TransitionAsync(scenario.Manager, scenario.TenantId, await CreateDraftAsync(scenario), "send");

        var response = await scenario.Manager.PostAsync(ActionUrl(scenario, sent, "resend-email"), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<PurchaseOrderDetailDto>(Ct))!;
        Assert.Equal(("Sent", sent.Version), (detail.Email!.Status, detail.Version));
        Assert.Equal(2, Emails.SentTo(address).Count);
    }

    [Fact]
    public async Task ResendEmailOfADraftIs409EmailNotAllowed()
    {
        using var scenario = await NewScenarioAsync(Factory, FakePurchaseOrderEmail.NewAddress());

        var response = await scenario.Manager.PostAsync(ActionUrl(scenario, await CreateDraftAsync(scenario), "resend-email"), null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("purchases.order.email_not_allowed", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ResendEmailWithoutASupplierEmailIs422()
    {
        using var scenario = await NewScenarioAsync(Factory);
        var sent = await TransitionAsync(scenario.Manager, scenario.TenantId, await CreateDraftAsync(scenario), "send");

        var response = await scenario.Manager.PostAsync(ActionUrl(scenario, sent, "resend-email"), null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("purchases.order.supplier_email_missing", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ResendEmailWithoutTheManagePermissionIs403()
    {
        using var scenario = await NewScenarioAsync(Factory, FakePurchaseOrderEmail.NewAddress());
        var sent = await TransitionAsync(scenario.Manager, scenario.TenantId, await CreateDraftAsync(scenario), "send");
        using var reader = CreateReader(Factory, scenario.Tenant);

        var response = await reader.PostAsync(ActionUrl(scenario, sent, "resend-email"), null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
