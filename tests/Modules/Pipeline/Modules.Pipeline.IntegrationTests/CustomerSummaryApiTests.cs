using System.Net;
using System.Net.Http.Json;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 D36 y §6.4: la sección de negocios de la ficha del cliente que se abre desde la conversación, dibujada
/// sin lógica propia. Sin la capacidad, sin permiso o desde otro tenant es 403 y la ficha oculta la sección.</summary>
public sealed class CustomerSummaryApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client, Guid CustomerId);

    private static async Task<Setup> SetupAsync(QepApiFactory factory, string connectionString, bool messaging = true)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        if (messaging)
        {
            await EnableMessagingAsync(connectionString, tenant.TenantId);
        }

        var (customerId, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura");
        return new Setup(factory, connectionString, tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions), customerId);
    }

    private static string SummaryUrl(Guid tenantId, Guid customerId, Guid? conversationId = null) =>
        $"{PipelineUrl(tenantId)}/customers/{customerId}/summary{(conversationId is { } id ? $"?conversationId={id}" : string.Empty)}";

    private static async Task<CustomerDealsSummaryDto> SummaryAsync(HttpClient client, Guid tenantId, Guid customerId, Guid? conversationId = null) =>
        (await client.GetFromJsonAsync<CustomerDealsSummaryDto>(SummaryUrl(tenantId, customerId, conversationId), Ct))!;

    private static async Task<DealDetailDto> CreateAsync(Setup setup, string title)
    {
        var response = await setup.Client.PostAsJsonAsync($"{PipelineUrl(setup.Tenant.TenantId)}/deals",
            new CreateDealRequest(setup.CustomerId, title, 100m, null, null, null), Ct);
        return (await response.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
    }

    [Fact]
    public async Task TheSheetShowsTheOpenDealsAndTheLastClosedAsContext()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var lost = await CreateAsync(setup, "Perdido antes");
        var price = (await setup.Client.GetFromJsonAsync<ReasonListDto>($"{PipelineUrl(setup.Tenant.TenantId)}/settings/loss-reasons", Ct))!
            .Items.Single(item => item.Name == "Precio").Id;
        (await setup.Client.PostAsJsonAsync($"{PipelineUrl(setup.Tenant.TenantId)}/deals/{lost.Id}/lose",
            new LoseDealRequest(price, null, lost.Version), Ct)).EnsureSuccessStatusCode();
        var first = await CreateAsync(setup, "Primero");
        var second = await CreateAsync(setup, "Segundo");

        var summary = await SummaryAsync(setup.Client, setup.Tenant.TenantId, setup.CustomerId);

        Assert.Equal(setup.CustomerId, summary.CustomerId);
        Assert.Equal([second.Id, first.Id], summary.OpenDeals.Select(deal => deal.Id));
        Assert.All(summary.OpenDeals, deal =>
        {
            Assert.Equal("Nuevo", deal.StageName);
            Assert.False(string.IsNullOrWhiteSpace(deal.OwnerName));
            Assert.Equal(((decimal?)100m, "COP", false), (deal.Amount, deal.Currency, deal.IsStalled));
        });
        Assert.NotNull(summary.LastClosedDeal);
        Assert.Equal((lost.Id, "Lost", "Precio"), (summary.LastClosedDeal.Id, summary.LastClosedDeal.Outcome, summary.LastClosedDeal.LossReasonName));
        Assert.Null(summary.Triage);
    }

    // §6.4: Pending ofrece create y dismiss (sólo con manage); después de descartar, Decided; con un negocio abierto, NotApplicable.
    [Fact]
    public async Task TheTriageStateFollowsTheConversation()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        using var reader = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ReadPermissions);
        var conversationId = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, setup.CustomerId,
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-5));
        var spam = (await setup.Client.GetFromJsonAsync<ReasonListDto>($"{PipelineUrl(setup.Tenant.TenantId)}/settings/dismiss-reasons", Ct))!
            .Items.Single(item => item.Name == "Spam").Id;

        var pending = (await SummaryAsync(setup.Client, setup.Tenant.TenantId, setup.CustomerId, conversationId)).Triage!;
        var pendingForReader = (await SummaryAsync(reader, setup.Tenant.TenantId, setup.CustomerId, conversationId)).Triage!;
        (await setup.Client.PostAsJsonAsync($"{PipelineUrl(setup.Tenant.TenantId)}/triage/{conversationId}",
            new DecideTriageRequest("dismiss", null, spam), Ct)).EnsureSuccessStatusCode();
        var decided = (await SummaryAsync(setup.Client, setup.Tenant.TenantId, setup.CustomerId, conversationId)).Triage!;
        await CreateAsync(setup, "Abierto");
        var notApplicable = (await SummaryAsync(setup.Client, setup.Tenant.TenantId, setup.CustomerId, conversationId)).Triage!;

        Assert.Equal(("Pending", conversationId), (pending.Status, pending.ConversationId));
        Assert.Equal(["create", "dismiss"], pending.AvailableActions);
        Assert.Empty(pendingForReader.AvailableActions);
        Assert.Equal("Decided", decided.Status);
        Assert.Empty(decided.AvailableActions);
        Assert.Equal(("Dismissed", "Spam"), (decided.LastDecision!.Kind, decided.LastDecision.DismissReasonName));
        Assert.Equal("NotApplicable", notApplicable.Status);
    }

    // D30: sin messaging no hay triage que ofrecer.
    [Fact]
    public async Task WithoutMessagingTheTriageIsNotApplicable()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString, messaging: false);
        using var _ = setup.Client;
        var conversationId = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, setup.CustomerId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var triage = (await SummaryAsync(setup.Client, setup.Tenant.TenantId, setup.CustomerId, conversationId)).Triage!;

        Assert.Equal("NotApplicable", triage.Status);
        Assert.Empty(triage.AvailableActions);
    }

    [Fact]
    public async Task AConversationOfAnotherCustomerIs404()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (otherCustomer, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Pedro");
        var otherConversation = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, otherCustomer, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var response = await setup.Client.GetAsync(SummaryUrl(setup.Tenant.TenantId, setup.CustomerId, otherConversation), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(PipelineErrorCodes.TriageConversationNotFound, (await ProblemAsync(response)).Code);
    }

    // Review Focus 4: el cliente de otro tenant bajo la ruta propia es 404; la ruta ajena es 403.
    [Fact]
    public async Task AForeignCustomerIs404AndAnotherTenantsRouteIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setupA = await SetupAsync(factory, connectionString);
        var setupB = await SetupAsync(factory, connectionString);
        using var a = setupA.Client;
        using var b = setupB.Client;

        var foreignCustomer = await b.GetAsync(SummaryUrl(setupB.Tenant.TenantId, setupA.CustomerId), Ct);
        var foreignRoute = await b.GetAsync(SummaryUrl(setupA.Tenant.TenantId, setupA.CustomerId), Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreignCustomer.StatusCode);
        Assert.Equal(PipelineErrorCodes.CustomerNotFound, (await ProblemAsync(foreignCustomer)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, foreignRoute.StatusCode);
    }

    // D36: sin la capacidad o sin permiso, 403, y la ficha oculta la sección.
    [Fact]
    public async Task WithoutTheCapabilityOrThePermissionIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        using var noPermission = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, "customers.customer.read");

        var forbidden = await noPermission.GetAsync(SummaryUrl(setup.Tenant.TenantId, setup.CustomerId), Ct);
        await DisableModuleAsync(connectionString, setup.Tenant.TenantId, "pipeline");
        var disabled = await setup.Client.GetAsync(SummaryUrl(setup.Tenant.TenantId, setup.CustomerId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(disabled)).Code);
    }
}
