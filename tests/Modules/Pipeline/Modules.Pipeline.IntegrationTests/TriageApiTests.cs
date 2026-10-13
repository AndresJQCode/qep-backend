using System.Net;
using System.Net.Http.Json;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-13 §5.1, D20, D29, D30, D35 y D37: la cola de conversaciones pendientes de triage y las dos decisiones.</summary>
public sealed class TriageApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client, Guid OwnerMemberId);

    private static async Task<Setup> SetupAsync(QepApiFactory factory, string connectionString, bool messaging = true)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        if (messaging)
        {
            await EnableMessagingAsync(connectionString, tenant.TenantId);
        }

        var owner = await OwnerMembershipIdAsync(connectionString, tenant);
        return new Setup(factory, connectionString, tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions), owner);
    }

    private static string TriageUrl(Guid tenantId) => $"{PipelineUrl(tenantId)}/triage";

    private static async Task<TriageListDto> ListAsync(Setup setup) =>
        (await setup.Client.GetFromJsonAsync<TriageListDto>(TriageUrl(setup.Tenant.TenantId), Ct))!;

    private static Task<HttpResponseMessage> DecideAsync(Setup setup, Guid conversationId, DecideTriageRequest request) =>
        setup.Client.PostAsJsonAsync($"{TriageUrl(setup.Tenant.TenantId)}/{conversationId}", request, Ct);

    [Fact]
    public async Task OnlyPendingConversationsAreListedNewestInboundFirst()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var now = DateTimeOffset.UtcNow;
        var (older, olderName) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Pedro");
        var (newer, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Laura");
        var (withDeal, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Ana");
        var olderConversation = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, older, now.AddDays(-1), now.AddDays(-1));
        var newerConversation = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, newer, now.AddHours(-1), now.AddHours(-1));
        await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, withDeal, now, now);
        await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, older, now, now, resolved: true);
        await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, customerId: null, now, now);
        (await setup.Client.PostAsJsonAsync($"{PipelineUrl(setup.Tenant.TenantId)}/deals",
            new CreateDealRequest(withDeal, "Dotación", null, null, null, null), Ct)).EnsureSuccessStatusCode();

        var list = await ListAsync(setup);

        Assert.Equal((true, 2), (list.Enabled, list.Count));
        Assert.Equal([newerConversation, olderConversation], list.Items.Select(item => item.ConversationId));
        Assert.Equal((older, olderName, false), (list.Items[1].CustomerId, list.Items[1].CustomerName, list.Items[1].CustomerIsComplete));
    }

    // D37: máximo 200 con el Count real.
    [Fact]
    public async Task TheQueueBringsAtMostTwoHundredWithTheRealCount()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (customerId, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Laura");
        await ExecuteAsync(connectionString,
            """
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, user_id, customer_id, status, unread_count,
                last_inbound_at, last_activity_at, created_at, updated_at, version)
            SELECT gen_random_uuid(), @t, gen_random_uuid(), 'CO.' || md5(n::text), @c, 'Open', 0,
                now() - make_interval(mins => n), now(), now(), now(), 1
            FROM generate_series(1, 201) AS n
            """,
            ("t", setup.Tenant.TenantId), ("c", customerId));

        var list = await ListAsync(setup);

        Assert.Equal((201, 200), (list.Count, list.Items.Count));
        Assert.True(list.Items[0].LastInboundAt > list.Items[199].LastInboundAt);
    }

    // D29: crear deja un negocio de la conversación y la saca de la cola; una segunda decisión llega tarde.
    [Fact]
    public async Task CreatingADealFromTheChatTakesItOutOfTheQueue()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (customerId, customerName) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Laura");
        var conversationId = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, customerId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var response = await DecideAsync(setup, conversationId, new DecideTriageRequest("create", null, null));
        var late = await DecideAsync(setup, conversationId, new DecideTriageRequest("create", null, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<TriageResultDto>(Ct))!;
        Assert.Equal((conversationId, "Created"), (result.ConversationId, result.Decision));
        Assert.NotNull(result.Deal);
        Assert.Equal(("Conversation", (Guid?)conversationId, setup.OwnerMemberId, customerName), (result.Deal.Source, result.Deal.OriginConversationId, result.Deal.OwnerMemberId, result.Deal.Title));
        Assert.Equal(("Nuevo", (decimal?)null), (result.Deal.StageName, result.Deal.Amount));
        Assert.Empty((await ListAsync(setup)).Items);
        Assert.Equal(PipelineErrorCodes.TriageNotPending, (await ProblemAsync(late)).Code);
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = 'pipeline.triage.created'", ("t", setup.Tenant.TenantId)));
    }

    // D38: dos asesores deciden la misma conversación a la vez. Gana uno; el otro sale not_pending (si llegó después del
    // commit) o already_decided (si chocó con el índice), y queda un solo negocio.
    [Fact]
    public async Task TwoConcurrentDecisionsLeaveOneDeal()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        using var second = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ManagePermissions);
        var (customerId, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Laura");
        var conversationId = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, customerId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var url = $"{TriageUrl(setup.Tenant.TenantId)}/{conversationId}";

        var responses = await Task.WhenAll(
            setup.Client.PostAsJsonAsync(url, new DecideTriageRequest("create", null, null), Ct),
            second.PostAsJsonAsync(url, new DecideTriageRequest("create", null, null), Ct));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.UnprocessableEntity);
        Assert.Contains((await ProblemAsync(loser)).Code, new[] { PipelineErrorCodes.TriageNotPending, PipelineErrorCodes.TriageAlreadyDecided });
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM pipeline.deals WHERE customer_id = @c", ("c", customerId)));
    }

    // D20: el descarte no es para siempre; D29: sólo create y dismiss.
    [Fact]
    public async Task ADismissalHidesItUntilTheCustomerWritesAgain()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (customerId, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Laura");
        var conversationId = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, customerId, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-5));
        var spam = (await setup.Client.GetFromJsonAsync<ReasonListDto>($"{PipelineUrl(setup.Tenant.TenantId)}/settings/dismiss-reasons", Ct))!
            .Items.Single(item => item.Name == "Spam").Id;

        var link = await DecideAsync(setup, conversationId, new DecideTriageRequest("link", null, null));
        var withoutReason = await DecideAsync(setup, conversationId, new DecideTriageRequest("dismiss", null, null));
        var unknownReason = await DecideAsync(setup, conversationId, new DecideTriageRequest("dismiss", null, Guid.CreateVersion7()));
        var dismissed = await DecideAsync(setup, conversationId, new DecideTriageRequest("dismiss", null, spam));
        var hidden = await ListAsync(setup);
        await ExecuteAsync(connectionString, "UPDATE messaging.conversations SET last_inbound_at = now() + interval '1 minute' WHERE id = @id", ("id", conversationId));
        var back = await ListAsync(setup);

        Assert.Equal(["action"], (await ProblemAsync(link)).ErrorKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(PipelineErrorCodes.TriageDismissReasonRequired, (await ProblemAsync(withoutReason)).Code);
        Assert.Equal(PipelineErrorCodes.TriageDismissReasonNotFound, (await ProblemAsync(unknownReason)).Code);
        Assert.Equal("Dismissed", (await dismissed.Content.ReadFromJsonAsync<TriageResultDto>(Ct))!.Decision);
        Assert.Empty(hidden.Items);
        Assert.Equal(conversationId, Assert.Single(back.Items).ConversationId);
    }

    // D30: sin messaging, la cola está vacía y oculta, y decidir es 403 con el código del módulo.
    [Fact]
    public async Task WithoutMessagingTheQueueIsDisabledAndDecisionsAre403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString, messaging: false);
        using var _ = setup.Client;
        var (customerId, _) = await SeedCustomerAsync(factory, setup.Tenant.TenantId, "Laura");
        var conversationId = await SeedConversationAsync(factory, connectionString, setup.Tenant.TenantId, customerId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var list = await ListAsync(setup);
        var decide = await DecideAsync(setup, conversationId, new DecideTriageRequest("create", null, null));

        Assert.Equal((false, 0), (list.Enabled, list.Count));
        Assert.Empty(list.Items);
        Assert.Equal(HttpStatusCode.Forbidden, decide.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(decide)).Code);
    }

    // Review Focus 4: la conversación de otro tenant bajo la ruta propia es 404, no 403 ni "no pendiente".
    [Fact]
    public async Task AForeignConversationIs404()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var otherTenant = Guid.CreateVersion7();
        var (foreignCustomer, _) = await SeedCustomerAsync(factory, otherTenant, "Laura");
        var foreignConversation = await SeedConversationAsync(factory, connectionString, otherTenant, foreignCustomer, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var response = await DecideAsync(setup, foreignConversation, new DecideTriageRequest("create", null, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(PipelineErrorCodes.TriageConversationNotFound, (await ProblemAsync(response)).Code);
    }
}
