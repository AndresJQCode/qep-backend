using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-11 §6.3 de punta a punta contra Postgres y el Graph de mentira: audiencia congelada, envío con la plantilla y sus
/// variables, mensaje y destinatario atados en una transacción, reintentos, la detención de la decisión 15, el opt-out de 131050,
/// recuperación de leases sin reenviar, cierre con su auditoría atómica y dos pods que nunca mandan lo mismo dos veces.</summary>
public sealed class CampaignDispatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Catalog = """
        {"data":[
          {"id":"1","name":"promo","language":"es","status":"APPROVED","category":"MARKETING",
           "components":[{"type":"BODY","text":"Hola {{1}}, tu código es {{2}}"}]},
          {"id":"2","name":"aviso","language":"es","status":"APPROVED","category":"UTILITY",
           "components":[{"type":"BODY","text":"Hola {{1}}, tu pedido salió"}]}
        ]}
        """;

    private sealed record Fixture(
        QepApiFactory Pod, WebApplicationFactory<Program> Host, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, Guid MemberId, HttpClient Client);

    private static string CampaignsUrl(Fixture f) => $"/api/v1/tenants/{f.Tenant.TenantId}/messaging/campaigns";

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, int messagesPerSecond = 20)
    {
        var connectionString = database.GetConnectionString();
        var pod = new QepApiFactory(connectionString);
        var host = pod.WithWebHostBuilder(builder => builder.UseSetting("Messaging:Campaigns:MessagesPerSecond", messagesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        pod.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        ScriptSends(pod.MetaHandler);
        var member = await OwnerMembershipIdAsync(connectionString, tenant);
        return new Fixture(pod, host, connectionString, tenant, connectionId, member, CreateClient(host, tenant.OwnerUserId, tenant.TenantId, CampaignPermissions));
    }

    /// <summary>Cada envío a <c>/111/messages</c> contesta con un wamid distinto; <paramref name="script"/> puede sustituir la respuesta del enésimo.
    /// <paramref name="prefix"/> distingue a dos Metas de mentira que atienden la misma conexión: un wamid repetido violaría
    /// <c>IX_messages_connection_wamid</c>, cosa que el Graph real nunca hace.</summary>
    private static void ScriptSends(FakeMetaGraphHandler meta, Func<int, HttpResponseMessage?>? script = null, string prefix = "c")
    {
        var counter = 0;
        meta.RespondWith("/111/messages", _ =>
        {
            var number = Interlocked.Increment(ref counter);
            return script?.Invoke(number)
                ?? FakeMetaGraphHandler.Json(HttpStatusCode.OK, $$"""{"messaging_product":"whatsapp","messages":[{"id":"wamid.{{prefix}}{{number}}"}]}""");
        });
    }

    private static object Input(Fixture f, Guid[] customerIds, string template = "promo", string name = "Promo", string? scheduledAt = null) =>
        new
        {
            name,
            connectionId = f.ConnectionId,
            template = new { name = template, language = "es" },
            parameters = template == "promo"
                ? new Dictionary<string, object> { ["1"] = new { source = "customerName" }, ["2"] = new { source = "text", value = "A1234" } }
                : new Dictionary<string, object> { ["1"] = new { source = "customerName" } },
            audience = new { tagIds = Array.Empty<Guid>(), customerIds },
            scheduledAt,
        };

    private static async Task<Guid> LaunchAsync(Fixture f, object input, bool launch = true)
    {
        var created = await SendAsync(f.Client, HttpMethod.Post, CampaignsUrl(f), input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        if (launch)
        {
            var launched = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignsUrl(f)}/{id}/launch", new { expectedVersion = 1 });
            Assert.Equal(HttpStatusCode.OK, launched.StatusCode);
        }

        return id;
    }

    private static Task<string> CampaignStatusAsync(Fixture f, Guid id) =>
        ScalarAsync<string>(f.ConnectionString, "SELECT status FROM messaging.campaigns WHERE id = @id", ("id", id));

    private static Task<string> RecipientStatusAsync(Fixture f, string name) =>
        ScalarAsync<string>(f.ConnectionString, "SELECT status FROM messaging.campaign_recipients WHERE customer_name = @n", ("n", name));

    private static int MetaSends(FakeMetaGraphHandler meta) =>
        meta.Requests.Count(request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal));

    private static (string Name, string Phone)[] ThreeCustomers() =>
        [("Ana Ruiz", "300 111 2222"), ("Beto Díaz", "310 222 3333"), ("Carla Sol", "320 333 4444")];

    [Fact]
    public async Task ALaunchedCampaignFreezesItsAudienceSendsTheTemplateAndCompletes()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ThreeCustomers());
        // Beto apagó las promociones: la plantilla es de marketing, así que se omite.
        await ExecuteAsync(f.ConnectionString, "UPDATE customers.customers SET marketing_opt_out = true WHERE id = @id", ("id", customers[1]));
        var id = await LaunchAsync(f, Input(f, [.. customers]));

        await DrainCampaignsAsync(f.Host);

        Assert.Equal(2, MetaSends(f.Pod.MetaHandler));
        Assert.Equal("Completed", await CampaignStatusAsync(f, id));
        Assert.Equal(("Sent", "Skipped", "Sent"), (await RecipientStatusAsync(f, "Ana Ruiz"), await RecipientStatusAsync(f, "Beto Díaz"), await RecipientStatusAsync(f, "Carla Sol")));
        Assert.Equal("opted_out", await ScalarAsync<string>(f.ConnectionString, "SELECT failure_code FROM messaging.campaign_recipients WHERE customer_name = 'Beto Díaz'"));

        var ana = Assert.Single(f.Pod.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("/111/messages", StringComparison.Ordinal) && request.Body!.Contains("573001112222", StringComparison.Ordinal));
        using var payload = JsonDocument.Parse(ana.Body!);
        Assert.Equal(("template", "promo", "es"), (
            payload.RootElement.GetProperty("type").GetString(),
            payload.RootElement.GetProperty("template").GetProperty("name").GetString(),
            payload.RootElement.GetProperty("template").GetProperty("language").GetProperty("code").GetString()));
        var parameters = payload.RootElement.GetProperty("template").GetProperty("components")[0].GetProperty("parameters");
        Assert.Equal(("Ana Ruiz", "A1234"), (parameters[0].GetProperty("text").GetString(), parameters[1].GetProperty("text").GetString()));
        Assert.StartsWith("qep:", payload.RootElement.GetProperty("biz_opaque_callback_data").GetString(), StringComparison.Ordinal);

        // El mensaje y el destinatario quedan atados; la conversación nace sin asignar; envía a nombre de quien creó la campaña.
        var row = await ScalarAsync<string>(f.ConnectionString,
            """
            SELECT m.kind || '|' || m.status || '|' || m.direction || '|' || (m.wamid IS NOT NULL) || '|' || (m.sent_by_member_id = @member) || '|' || (m.client_id = r.id)
                   || '|' || (c.assigned_member_id IS NULL) || '|' || (r.conversation_id = c.id) || '|' || (m.text LIKE 'Hola Ana Ruiz, tu código es A1234%')
              FROM messaging.campaign_recipients r
              JOIN messaging.messages m ON m.id = r.message_id
              JOIN messaging.conversations c ON c.id = m.conversation_id
             WHERE r.customer_name = 'Ana Ruiz'
            """, ("member", f.MemberId));
        Assert.Equal("11|1|2|true|true|true|true|true|true", row);
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2 AND status = 1 AND wamid IS NOT NULL"));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations WHERE assigned_member_id IS NULL"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'messaging.campaign.completed' AND resource_id = @id AND actor_type = 'System'", ("id", id.ToString())));

        // Un segundo tick no reenvía nada.
        await DrainCampaignsAsync(f.Host);
        Assert.Equal(2, MetaSends(f.Pod.MetaHandler));
    }

    /// <summary>Spec 2026-10-16 §3.1: una campaña sale por el despachador, no por <c>OutboundGuards.SettleAsync</c>, así que no es «responder»:
    /// ni resta el contador de la conversación del destinatario ni manda acuse de leído.</summary>
    [Fact]
    public async Task ACampaignNeverTouchesTheUnreadMark()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"));
        using var anonymous = f.Host.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001112222", "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(f.Host);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET unread_count = 2");
        await LaunchAsync(f, Input(f, [.. customers]));

        await DrainCampaignsAsync(f.Host);

        Assert.Equal("Sent", await RecipientStatusAsync(f, "Ana Ruiz"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
        Assert.Equal(2, await ScalarAsync<int>(f.ConnectionString, "SELECT unread_count FROM messaging.conversations"));
        Assert.DoesNotContain(f.Pod.MetaHandler.Requests, IsReadReceipt);
    }

    [Fact]
    public async Task AnUtilityTemplateReachesWhoOptedOutOfPromotions()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ThreeCustomers());
        await ExecuteAsync(f.ConnectionString, "UPDATE customers.customers SET marketing_opt_out = true WHERE id = @id", ("id", customers[1]));
        await ExecuteAsync(f.ConnectionString, "UPDATE customers.customers SET is_active = false WHERE id = @id", ("id", customers[2]));
        await LaunchAsync(f, Input(f, [.. customers], template: "aviso"));

        await DrainCampaignsAsync(f.Host);

        Assert.Equal(("Sent", "Sent", "Skipped"), (await RecipientStatusAsync(f, "Ana Ruiz"), await RecipientStatusAsync(f, "Beto Díaz"), await RecipientStatusAsync(f, "Carla Sol")));
        Assert.Equal("inactive", await ScalarAsync<string>(f.ConnectionString, "SELECT failure_code FROM messaging.campaign_recipients WHERE customer_name = 'Carla Sol'"));
        Assert.Equal(2, MetaSends(f.Pod.MetaHandler));
    }

    [Fact]
    public async Task ARetriableErrorBacksOffAndTheRetryReusesTheSameMessage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        f.Pod.MetaHandler.Reset();
        f.Pod.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        ScriptSends(f.Pod.MetaHandler, number => number == 1 ? FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(130429)) : null);
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"));
        var id = await LaunchAsync(f, Input(f, [.. customers]));

        await DrainCampaignsAsync(f.Host);

        Assert.Equal(("Pending", 1), (
            await RecipientStatusAsync(f, "Ana Ruiz"),
            await ScalarAsync<int>(f.ConnectionString, "SELECT attempts FROM messaging.campaign_recipients")));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString, "SELECT next_attempt_at > @t FROM messaging.campaign_recipients", ("t", DateTimeOffset.UtcNow.AddSeconds(20))));
        Assert.Equal("Sending", await CampaignStatusAsync(f, id));
        var messageId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT message_id FROM messaging.campaign_recipients");

        // Decisión del owner 2026-10-12 (P3): el 130429 bajó el ritmo de la conexión a la mitad y la pausó 30 s, para todas las réplicas.
        Assert.Equal(10d, await ScalarAsync<double>(f.ConnectionString, "SELECT rate FROM messaging.connection_send_budgets WHERE connection_id = @c", ("c", f.ConnectionId)));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString, "SELECT throttled_until > now() FROM messaging.connection_send_budgets WHERE connection_id = @c", ("c", f.ConnectionId)));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.connection_send_budgets SET throttled_until = now() - interval '1 minute', refilled_at = now() - interval '1 minute'");
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.campaign_recipients SET next_attempt_at = now() - interval '1 hour'");
        await DrainCampaignsAsync(f.Host);

        Assert.Equal(2, MetaSends(f.Pod.MetaHandler));
        Assert.Equal(("Sent", 2, messageId), (
            await RecipientStatusAsync(f, "Ana Ruiz"),
            await ScalarAsync<int>(f.ConnectionString, "SELECT attempts FROM messaging.campaign_recipients"),
            await ScalarAsync<Guid>(f.ConnectionString, "SELECT message_id FROM messaging.campaign_recipients")));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        Assert.Equal("Completed", await CampaignStatusAsync(f, id));
    }

    [Fact]
    public async Task A5xxIsUnknownOutcomeAndIsNeverResent()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        f.Pod.MetaHandler.Reset();
        f.Pod.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        ScriptSends(f.Pod.MetaHandler, _ => FakeMetaGraphHandler.Json(HttpStatusCode.BadGateway, "{}"));
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"));
        var id = await LaunchAsync(f, Input(f, [.. customers]));

        await DrainCampaignsAsync(f.Host);
        await DrainCampaignsAsync(f.Host);

        Assert.Equal(1, MetaSends(f.Pod.MetaHandler));
        Assert.Equal("Failed", await RecipientStatusAsync(f, "Ana Ruiz"));
        Assert.Equal("unknown_outcome", await ScalarAsync<string>(f.ConnectionString, "SELECT failure_code FROM messaging.campaign_recipients"));
        Assert.Equal("4|-1", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || failure_code FROM messaging.messages WHERE direction = 2"));
        Assert.Equal("Completed", await CampaignStatusAsync(f, id));
    }

    [Fact]
    public async Task AMarketingOptOutFromMetaFailsTheRecipientAndMarksTheCustomerWithoutStoppingTheCampaign()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        f.Pod.MetaHandler.Reset();
        f.Pod.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        ScriptSends(f.Pod.MetaHandler, number => number == 1 ? FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(131050)) : null);
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"));
        var id = await LaunchAsync(f, Input(f, [.. customers]));

        await DrainCampaignsAsync(f.Host);

        Assert.Equal("131050", await ScalarAsync<string>(f.ConnectionString, "SELECT failure_code FROM messaging.campaign_recipients"));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString, "SELECT marketing_opt_out AND marketing_opt_out_source = 'meta' FROM customers.customers WHERE id = @id", ("id", customers[0])));
        Assert.Equal("Completed", await CampaignStatusAsync(f, id));
    }

    [Fact]
    public async Task AFailureMetaReportsAfterAcceptingFailsTheRecipientWithItsReasonAndMakesItRetriable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"));
        var id = await LaunchAsync(f, Input(f, [.. customers]));
        await DrainCampaignsAsync(f.Host);
        Assert.Equal("Sent", await RecipientStatusAsync(f, "Ana Ruiz"));
        var wamid = await ScalarAsync<string>(f.ConnectionString, "SELECT wamid FROM messaging.messages WHERE direction = 2");

        using var webhook = f.Host.CreateClient();
        await PostWebhookAsync(webhook, MetaPayloads.Status("111", wamid, "failed", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, 131042, "Business eligibility payment issue"));
        await DrainDeliveriesAsync(f.Host);

        Assert.Equal("Failed", await RecipientStatusAsync(f, "Ana Ruiz"));
        var detail = await f.Client.GetFromJsonAsync<JsonElement>($"{CampaignsUrl(f)}/{id}", Ct);
        var counts = detail.GetProperty("counts");
        Assert.Equal((0, 1, 1), (counts.GetProperty("sent").GetInt32(), counts.GetProperty("failed").GetInt32(), counts.GetProperty("retriable").GetInt32()));
        var recipients = await f.Client.GetFromJsonAsync<JsonElement>($"{CampaignsUrl(f)}/{id}/recipients", Ct);
        var recipient = recipients.GetProperty("items")[0];
        Assert.Equal("131042", recipient.GetProperty("failureCode").GetString());
        Assert.Equal(MessageFailureReasons.For(131042), recipient.GetProperty("failureReason").GetString());
        Assert.True(recipient.GetProperty("retriable").GetBoolean());
    }

    [Fact]
    public async Task APausedTemplateStopsTheCampaignAndSkipsWhatWasPending()
    {
        await using var database = await StartDatabaseAsync();
        // Dos por segundo: el worker reclama 4 por tick, así que de 6 clientes quedan 2 pendientes cuando la campaña se detiene.
        var f = await ArrangeAsync(database, messagesPerSecond: 2);
        using var pod = f.Pod;
        f.Pod.MetaHandler.Reset();
        f.Pod.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        ScriptSends(f.Pod.MetaHandler, _ => FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(132015)));
        var customers = await CreateCustomersAsync(f.Host, f.Tenant,
            ("Ana", "300 111 2201"), ("Beto", "300 111 2202"), ("Carla", "300 111 2203"), ("Diego", "300 111 2204"), ("Elena", "300 111 2205"), ("Fabio", "300 111 2206"));
        var id = await LaunchAsync(f, Input(f, [.. customers]));

        await DrainCampaignsAsync(f.Host);

        Assert.Equal("Failed", await CampaignStatusAsync(f, id));
        var detail = await f.Client.GetFromJsonAsync<JsonElement>($"{CampaignsUrl(f)}/{id}", Ct);
        Assert.Equal("132015", detail.GetProperty("failureCode").GetString());
        Assert.StartsWith("WhatsApp pausó esta plantilla", detail.GetProperty("failureReason").GetString(), StringComparison.Ordinal);
        // Los que ya estaban en vuelo fallan con el código de Meta; los que esperaban su turno del ritmo ya no se envían: Skipped campaign_failed.
        var failed = await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.campaign_recipients WHERE status = 'Failed' AND failure_code = '132015'");
        var skipped = await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.campaign_recipients WHERE status = 'Skipped' AND failure_code = 'campaign_failed'");
        Assert.InRange(failed, 1, 4);
        Assert.Equal(6L, failed + skipped);
        Assert.Equal(failed, MetaSends(f.Pod.MetaHandler));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.campaign.failed' AND resource_id = @id", ("id", id.ToString())));
        var sends = MetaSends(f.Pod.MetaHandler);

        await DrainCampaignsAsync(f.Host);

        Assert.Equal(sends, MetaSends(f.Pod.MetaHandler));
    }

    [Fact]
    public async Task RejectedCredentialsStopTheCampaignAndFlagTheConnection()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        f.Pod.MetaHandler.Reset();
        f.Pod.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        ScriptSends(f.Pod.MetaHandler, _ => FakeMetaGraphHandler.Json(HttpStatusCode.Unauthorized, FakeMetaGraphHandler.GraphError(190)));
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"));
        var id = await LaunchAsync(f, Input(f, [.. customers]));

        await DrainCampaignsAsync(f.Host);

        Assert.Equal("Failed", await CampaignStatusAsync(f, id));
        Assert.Equal("190", await ScalarAsync<string>(f.ConnectionString, "SELECT failure_code FROM messaging.campaigns WHERE id = @id", ("id", id)));
        Assert.Equal("NeedsAttention", await ScalarAsync<string>(f.ConnectionString, "SELECT status FROM integrations.connections WHERE id = @id", ("id", f.ConnectionId)));
    }

    [Fact]
    public async Task ACancelledCampaignSendsNothingMore()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ThreeCustomers());
        var id = await LaunchAsync(f, Input(f, [.. customers]));
        var cancelled = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignsUrl(f)}/{id}/cancel", new { expectedVersion = 2 });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        await DrainCampaignsAsync(f.Host);

        Assert.Equal(0, MetaSends(f.Pod.MetaHandler));
        Assert.Equal("Cancelled", await CampaignStatusAsync(f, id));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.campaign_recipients"));
    }

    [Fact]
    public async Task ARecipientClaimedJustBeforeACancelIsSkippedNotSent()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"));
        var id = await LaunchAsync(f, Input(f, [.. customers]));
        using var scope = f.Host.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<CampaignDispatcher>();
        await dispatcher.PrepareTickAsync(Ct);
        var claimed = await dispatcher.ClaimAsync(20, Ct);
        Assert.Single(claimed);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.campaigns SET status = 'Cancelled' WHERE id = @id", ("id", id));

        await dispatcher.SendAsync(claimed[0], Ct);

        Assert.Equal(0, MetaSends(f.Pod.MetaHandler));
        Assert.Equal("Skipped", await RecipientStatusAsync(f, "Ana Ruiz"));
        Assert.Equal("campaign_cancelled", await ScalarAsync<string>(f.ConnectionString, "SELECT failure_code FROM messaging.campaign_recipients"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task AScheduledCampaignStartsWhenItsTimeComes()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"));
        var id = await LaunchAsync(f, Input(f, [.. customers], scheduledAt: DateTimeOffset.UtcNow.AddDays(3).ToString("o")));
        await DrainCampaignsAsync(f.Host);
        Assert.Equal(("Scheduled", 0), (await CampaignStatusAsync(f, id), MetaSends(f.Pod.MetaHandler)));

        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.campaigns SET scheduled_at = now() - interval '1 hour' WHERE id = @id", ("id", id));
        await DrainCampaignsAsync(f.Host);

        Assert.Equal(1, MetaSends(f.Pod.MetaHandler));
        Assert.Equal("Completed", await CampaignStatusAsync(f, id));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString, "SELECT started_at IS NOT NULL AND version > 2 FROM messaging.campaigns WHERE id = @id", ("id", id)));
    }

    [Fact]
    public async Task AnExpiredLeaseWithAMessageFailsAsUnknownOutcomeWithoutResendingAndOneWithoutGoesBackToPending()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var id = await LaunchAsync(f, Input(f, [Guid.CreateVersion7()]));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.campaigns SET audience_frozen_at = now() WHERE id = @id", ("id", id));
        var conversation = await SeedBsuidConversationAsync(f.Host, f.ConnectionString, f.Tenant.TenantId, f.ConnectionId, "CO.573009990001", "573009990001");
        var inFlightMessage = await SeedOutboundAsync(f.ConnectionString, conversation, f.Tenant.TenantId, f.ConnectionId, null, status: 4, failureCode: -1);
        await SeedSendingRecipientAsync(f, id, "En vuelo", "573009990001", messageId: inFlightMessage, conversationId: conversation, lockedUntil: "now() - interval '1 hour'");
        // Es el único que llega a enviarse, así que necesita un cliente que exista (el worker revisa su consentimiento, R6).
        var real = await CreateCustomersAsync(f.Host, f.Tenant, ("Sin mensaje", "300 999 0002"));
        await SeedSendingRecipientAsync(f, id, "Sin mensaje", "573009990002", messageId: null, conversationId: null, lockedUntil: "now() - interval '1 hour'", customerId: real[0]);
        await SeedSendingRecipientAsync(f, id, "Con lease vivo", "573009990003", messageId: null, conversationId: null, lockedUntil: "now() + interval '1 hour'");

        await DrainCampaignsAsync(f.Host);

        Assert.Equal("Failed", await RecipientStatusAsync(f, "En vuelo"));
        Assert.Equal("unknown_outcome", await ScalarAsync<string>(f.ConnectionString, "SELECT failure_code FROM messaging.campaign_recipients WHERE customer_name = 'En vuelo'"));
        Assert.Equal("Pending", await RecipientStatusAsync(f, "Sin mensaje"));
        Assert.Equal("Sending", await RecipientStatusAsync(f, "Con lease vivo"));
        Assert.Equal(0, MetaSends(f.Pod.MetaHandler));

        await DrainCampaignsAsync(f.Host);

        Assert.Equal("Sent", await RecipientStatusAsync(f, "Sin mensaje"));
        Assert.Equal(1, MetaSends(f.Pod.MetaHandler));
        Assert.Equal("Sending", await CampaignStatusAsync(f, id));
    }

    [Fact]
    public async Task TheCompletionAndItsAuditCommitTogetherOrNotAtAll()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"));
        var id = await LaunchAsync(f, Input(f, [.. customers]));
        await ExecuteAsync(f.ConnectionString,
            """
            CREATE FUNCTION audit.reject_campaign_completed() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.action = 'messaging.campaign.completed' THEN RAISE EXCEPTION 'audit refused'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER reject_campaign_completed BEFORE INSERT ON audit.entries FOR EACH ROW EXECUTE FUNCTION audit.reject_campaign_completed();
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => DrainCampaignsAsync(f.Host));

        Assert.Equal("Sending", await CampaignStatusAsync(f, id));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.campaign.completed'"));

        await ExecuteAsync(f.ConnectionString, "DROP TRIGGER reject_campaign_completed ON audit.entries");
        await DrainCampaignsAsync(f.Host);

        Assert.Equal("Completed", await CampaignStatusAsync(f, id));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.campaign.completed'"));
        // Y el envío de antes no se repitió.
        Assert.Equal(1, MetaSends(f.Pod.MetaHandler));
    }

    [Fact]
    public async Task TwoPodsDrainingTheSameCampaignNeverSendTheSameRecipientTwice()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var firstPod = f.Pod;
        using var secondPod = new QepApiFactory(f.ConnectionString);
        secondPod.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        ScriptSends(secondPod.MetaHandler, prefix: "pod2-");
        var id = await LaunchAsync(f, Input(f, [Guid.CreateVersion7()]));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.campaigns SET audience_frozen_at = now() WHERE id = @id", ("id", id));
        const int recipients = 60;
        var real = await CreateCustomersAsync(f.Host, f.Tenant, [.. Enumerable.Range(0, recipients).Select(index => ($"Cliente {index:D3}", $"300 {100 + index:D3} 2222"))]);
        for (var index = 0; index < recipients; index++)
        {
            await SeedPendingRecipientAsync(f, id, $"Cliente {index:D3}", $"5730099{index:D5}", real[index]);
        }

        for (var round = 0; round < 10 && await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.campaign_recipients WHERE status IN ('Pending','Sending')") > 0; round++)
        {
            await Task.WhenAll(DrainCampaignsAsync(f.Host), DrainCampaignsAsync(secondPod));
        }

        Assert.Equal(recipients, MetaSends(f.Pod.MetaHandler) + MetaSends(secondPod.MetaHandler));
        Assert.Equal((long)recipients, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.campaign_recipients WHERE status = 'Sent'"));
        Assert.Equal((long)recipients, await CountAsync(f.ConnectionString, "SELECT count(DISTINCT message_id) FROM messaging.campaign_recipients"));
        Assert.Equal((long)recipients, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        Assert.Equal("Completed", await CampaignStatusAsync(f, id));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.campaign.completed' AND resource_id = @id", ("id", id.ToString())));
    }

    [Fact]
    public async Task ARecipientReclaimedByAnotherWorkerAfterTheLeaseExpiredIsNotSentTwice()
    {
        // R1: el worker A reclamó, se frenó (GC, red) y su lease venció; la recuperación lo devolvió a Pending y B lo reclamó. Cuando A
        // despierta ya no es el dueño: no inserta mensaje ni llama a Meta.
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"));
        await LaunchAsync(f, Input(f, [.. customers]));
        using var scopeA = f.Host.Services.CreateScope();
        using var scopeB = f.Host.Services.CreateScope();
        var workerA = scopeA.ServiceProvider.GetRequiredService<CampaignDispatcher>();
        var workerB = scopeB.ServiceProvider.GetRequiredService<CampaignDispatcher>();
        await workerA.PrepareTickAsync(Ct);
        var claimedByA = Assert.Single(await workerA.ClaimAsync(20, Ct));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.campaign_recipients SET locked_until = now() - interval '1 hour'");
        await workerB.FinishTickAsync(Ct);
        var claimedByB = Assert.Single(await workerB.ClaimAsync(20, Ct));
        Assert.Equal((1, 2), (claimedByA.Attempts, claimedByB.Attempts));

        await workerA.SendAsync(claimedByA, Ct);

        Assert.Equal(0, MetaSends(f.Pod.MetaHandler));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        Assert.Equal(("Sending", 2), (await RecipientStatusAsync(f, "Ana Ruiz"), await ScalarAsync<int>(f.ConnectionString, "SELECT attempts FROM messaging.campaign_recipients")));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString, "SELECT message_id IS NULL FROM messaging.campaign_recipients"));

        await workerB.SendAsync(claimedByB, Ct);

        Assert.Equal(1, MetaSends(f.Pod.MetaHandler));
        Assert.Equal("Sent", await RecipientStatusAsync(f, "Ana Ruiz"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task ALateSettleFromAWorkerThatLostTheRecipientChangesNothing()
    {
        // R1: ni Settle ni Skip ni Retry pisan a quien ya es de otro: el token de intento no coincide.
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"));
        await LaunchAsync(f, Input(f, [.. customers]));
        using var scopeA = f.Host.Services.CreateScope();
        using var scopeB = f.Host.Services.CreateScope();
        var workerA = scopeA.ServiceProvider.GetRequiredService<CampaignDispatcher>();
        var workerB = scopeB.ServiceProvider.GetRequiredService<CampaignDispatcher>();
        await workerA.PrepareTickAsync(Ct);
        var stale = Assert.Single(await workerA.ClaimAsync(20, Ct));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.campaign_recipients SET locked_until = now() - interval '1 hour'");
        await workerB.FinishTickAsync(Ct);
        Assert.Single(await workerB.ClaimAsync(20, Ct));
        var store = scopeA.ServiceProvider.GetRequiredService<ICampaignDispatchStore>();

        await store.SkipAsync(stale, CampaignFailureCodes.CampaignCancelled, Ct);
        await store.ScheduleRetryAsync(stale, DateTimeOffset.UtcNow.AddHours(1), Ct);
        await store.DeferAsync(stale, DateTimeOffset.UtcNow.AddHours(1), Ct);
        await store.SettleFailedAsync(stale, prepared: null, CampaignFailureCodes.UnknownOutcome, graphCode: null, title: null, DateTimeOffset.UtcNow, Ct);

        Assert.Equal(("Sending", 2), (await RecipientStatusAsync(f, "Ana Ruiz"), await ScalarAsync<int>(f.ConnectionString, "SELECT attempts FROM messaging.campaign_recipients")));
    }

    [Fact]
    public async Task ADeferredRecipientGivesTheAttemptBackAndWaits()
    {
        // R4: DeferAsync reprograma sin consumir el intento que sumó el reclamo.
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"));
        await LaunchAsync(f, Input(f, [.. customers]));
        using var scope = f.Host.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<CampaignDispatcher>();
        await dispatcher.PrepareTickAsync(Ct);
        var claimed = Assert.Single(await dispatcher.ClaimAsync(20, Ct));
        Assert.Equal(1, claimed.Attempts);

        await scope.ServiceProvider.GetRequiredService<ICampaignDispatchStore>().DeferAsync(claimed, DateTimeOffset.UtcNow.AddSeconds(60), Ct);

        Assert.Equal(("Pending", 0), (await RecipientStatusAsync(f, "Ana Ruiz"), await ScalarAsync<int>(f.ConnectionString, "SELECT attempts FROM messaging.campaign_recipients")));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString, "SELECT next_attempt_at > now() + interval '30 seconds' AND locked_until IS NULL FROM messaging.campaign_recipients"));
        Assert.Empty(await dispatcher.ClaimAsync(20, Ct));
    }

    [Fact]
    public async Task AnExpiredLeaseWithoutAMessageAndNoAttemptsLeftFailsAsNotSentInsteadOfLoopingForever()
    {
        // R2: sin esto, un destinatario cuyo worker muere siempre antes de insertar el mensaje vuelve a Pending sin tope.
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var id = await LaunchAsync(f, Input(f, [Guid.CreateVersion7()]));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.campaigns SET audience_frozen_at = now() WHERE id = @id", ("id", id));
        await SeedSendingRecipientAsync(f, id, "Agotado", "573009990001", messageId: null, conversationId: null, lockedUntil: "now() - interval '1 hour'", attempts: CampaignDispatcher.MaxAttempts);
        await SeedSendingRecipientAsync(f, id, "Con margen", "573009990002", messageId: null, conversationId: null, lockedUntil: "now() - interval '1 hour'", attempts: CampaignDispatcher.MaxAttempts - 1);

        await using (var recovery = f.Host.Services.CreateAsyncScope())
        {
            await recovery.ServiceProvider.GetRequiredService<CampaignDispatcher>().FinishTickAsync(Ct);
        }

        Assert.Equal("Failed", await RecipientStatusAsync(f, "Agotado"));
        Assert.Equal("not_sent", await ScalarAsync<string>(f.ConnectionString, "SELECT failure_code FROM messaging.campaign_recipients WHERE customer_name = 'Agotado'"));
        Assert.Equal(CampaignFailureReasons.For(CampaignFailureCodes.NotSent), await ScalarAsync<string>(f.ConnectionString, "SELECT failure_reason FROM messaging.campaign_recipients WHERE customer_name = 'Agotado'"));
        Assert.Equal("Pending", await RecipientStatusAsync(f, "Con margen"));
        Assert.Equal(0, MetaSends(f.Pod.MetaHandler));
    }

    [Fact]
    public async Task OneCampaignWithUnreadableParametersFailsAloneAndDoesNotTakeTheWholeBatchDown()
    {
        // R2: antes, un JSON de parámetros roto hacía lanzar a ClaimAsync entero: cada tick reclamaba, fallaba y dejaba a todos en Sending.
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"), ("Beto Díaz", "310 222 3333"));
        var broken = await LaunchAsync(f, Input(f, [customers[0]], name: "Rota"));
        var healthy = await LaunchAsync(f, Input(f, [customers[1]], name: "Sana"));
        await ExecuteAsync(f.ConnectionString, """UPDATE messaging.campaigns SET parameters = '{"1": 5}'::jsonb WHERE id = @id""", ("id", broken));

        await DrainCampaignsAsync(f.Host);

        Assert.Equal("Failed", await CampaignStatusAsync(f, broken));
        Assert.Equal(CampaignFailureCodes.InvalidParameters, await ScalarAsync<string>(f.ConnectionString, "SELECT failure_code FROM messaging.campaigns WHERE id = @id", ("id", broken)));
        Assert.Equal(("Failed", CampaignFailureCodes.InvalidParameters), (
            await RecipientStatusAsync(f, "Ana Ruiz"),
            await ScalarAsync<string>(f.ConnectionString, "SELECT failure_code FROM messaging.campaign_recipients WHERE customer_name = 'Ana Ruiz'")));
        Assert.Equal("Sent", await RecipientStatusAsync(f, "Beto Díaz"));
        Assert.Equal("Completed", await CampaignStatusAsync(f, healthy));
        Assert.Equal(1, MetaSends(f.Pod.MetaHandler));
    }

    [Fact]
    public async Task AClaimTakesTurnsAcrossCampaignsSoALargeOneDoesNotStarveASmallOne()
    {
        // R5: dos campañas en la misma conexión; la grande tiene los destinatarios más viejos. Con un reclamo de 2, el orden estricto por
        // fecha le daría los dos cupos a la grande; el reparto por turnos le da uno a cada una.
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var large = await LaunchAsync(f, Input(f, [Guid.CreateVersion7()], name: "Grande"));
        var small = await LaunchAsync(f, Input(f, [Guid.CreateVersion7()], name: "Chica"));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.campaigns SET audience_frozen_at = now()");
        for (var index = 0; index < 5; index++)
        {
            await SeedPendingRecipientAsync(f, large, $"Grande {index}", $"573009910{index:D3}");
        }

        await SeedPendingRecipientAsync(f, small, "Chica 0", "573009920000");
        using var scope = f.Host.Services.CreateScope();

        var claimed = await scope.ServiceProvider.GetRequiredService<CampaignDispatcher>().ClaimAsync(1, Ct);

        Assert.Equal(2, claimed.Count);
        Assert.Contains(claimed, recipient => recipient.CampaignId == small);
        Assert.Contains(claimed, recipient => recipient.CampaignId == large);
    }

    [Fact]
    public async Task TheConnectionsAlsoTakeTurns()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var second = await SeedWhatsAppConnectionAsync(f.Host, f.Tenant.TenantId, "Soporte", "333", "444");
        var onFirst = await LaunchAsync(f, Input(f, [Guid.CreateVersion7()], name: "Ventas"));
        var onSecond = await LaunchAsync(f, Input(f, [Guid.CreateVersion7()], name: "Soporte"));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.campaigns SET connection_id = @c WHERE id = @id", ("c", second), ("id", onSecond));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.campaigns SET audience_frozen_at = now()");
        for (var index = 0; index < 5; index++)
        {
            await SeedPendingRecipientAsync(f, onFirst, $"Ventas {index}", $"573009910{index:D3}");
        }

        await SeedPendingRecipientAsync(f, onSecond, "Soporte 0", "573009920000");
        using var scope = f.Host.Services.CreateScope();

        var claimed = await scope.ServiceProvider.GetRequiredService<CampaignDispatcher>().ClaimAsync(1, Ct);

        Assert.Contains(claimed, recipient => recipient.CampaignId == onSecond);
    }

    [Fact]
    public async Task ARecipientWhoOptedOutAfterTheFreezeIsSkippedBeforeAnythingIsSent()
    {
        // R6: el congelado miró el consentimiento hace horas; al enviar se vuelve a mirar.
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"));
        await LaunchAsync(f, Input(f, [.. customers]));
        using var scope = f.Host.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<CampaignDispatcher>();
        await dispatcher.PrepareTickAsync(Ct);
        var claimed = Assert.Single(await dispatcher.ClaimAsync(20, Ct));
        await ExecuteAsync(f.ConnectionString, "UPDATE customers.customers SET marketing_opt_out = true WHERE id = @id", ("id", customers[0]));

        await dispatcher.SendAsync(claimed, Ct);

        Assert.Equal(0, MetaSends(f.Pod.MetaHandler));
        Assert.Equal(("Skipped", "opted_out"), (
            await RecipientStatusAsync(f, "Ana Ruiz"),
            await ScalarAsync<string>(f.ConnectionString, "SELECT failure_code FROM messaging.campaign_recipients")));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task AMarketingCampaignSkipsTheWholeNumberWhenOneOfItsCustomersOptedOut()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"), ("Ana Segunda", "300 111 2222"), ("Carla Sol", "320 333 4444"));
        await ExecuteAsync(f.ConnectionString, "UPDATE customers.customers SET marketing_opt_out = true WHERE id = @id", ("id", customers[0]));
        await LaunchAsync(f, Input(f, [.. customers]));

        await DrainCampaignsAsync(f.Host);

        Assert.Equal(("Skipped", "Skipped", "Sent"), (await RecipientStatusAsync(f, "Ana Ruiz"), await RecipientStatusAsync(f, "Ana Segunda"), await RecipientStatusAsync(f, "Carla Sol")));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.campaign_recipients WHERE failure_code = 'opted_out'"));
        Assert.Equal(1, MetaSends(f.Pod.MetaHandler));
    }

    [Fact]
    public async Task AnUtilityCampaignStillReachesANumberWithAnOptOut()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"), ("Ana Segunda", "300 111 2222"));
        await ExecuteAsync(f.ConnectionString, "UPDATE customers.customers SET marketing_opt_out = true WHERE id = @id", ("id", customers[0]));
        await LaunchAsync(f, Input(f, [.. customers], template: "aviso"));

        await DrainCampaignsAsync(f.Host);

        Assert.Equal(("Sent", "Sent"), (await RecipientStatusAsync(f, "Ana Ruiz"), await RecipientStatusAsync(f, "Ana Segunda")));
    }

    [Fact]
    public async Task AnOptOutOnTheNumberAfterTheFreezeSkipsTheOtherCustomerBeforeSending()
    {
        // R6/R7 con el opt-out por número (desvío A6): la bandera va en A y el destinatario que se re-verifica es B.
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var customers = await CreateCustomersAsync(f.Host, f.Tenant, ("Ana Ruiz", "300 111 2222"), ("Ana Segunda", "300 111 2222"));
        await LaunchAsync(f, Input(f, [customers[1]]));
        using var scope = f.Host.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<CampaignDispatcher>();
        await dispatcher.PrepareTickAsync(Ct);
        var claimed = Assert.Single(await dispatcher.ClaimAsync(20, Ct));
        await ExecuteAsync(f.ConnectionString, "UPDATE customers.customers SET marketing_opt_out = true WHERE id = @id", ("id", customers[0]));

        await dispatcher.SendAsync(claimed, Ct);

        Assert.Equal(0, MetaSends(f.Pod.MetaHandler));
        Assert.Equal(("Skipped", "opted_out"), (
            await RecipientStatusAsync(f, "Ana Segunda"),
            await ScalarAsync<string>(f.ConnectionString, "SELECT failure_code FROM messaging.campaign_recipients")));
    }

    [Fact]
    public async Task ACampaignCancelledWhileItsAudienceIsBeingWrittenGetsNoNewPendingRecipients()
    {
        // R9: el INSERT de cada lote sólo corre mientras la campaña sigue Sending.
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var id = await LaunchAsync(f, Input(f, [Guid.CreateVersion7()]));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.campaigns SET status = 'Cancelled' WHERE id = @id", ("id", id));
        using var scope = f.Host.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ICampaignDispatchStore>();
        var target = new CampaignFreezeTarget(id, f.Tenant.TenantId, "MARKETING", [], []);

        await store.AddRecipientsAsync(
            target,
            [new CampaignRecipientSeed(Guid.CreateVersion7(), "Ana", "573001112222", null, CampaignRecipientStatus.Pending, null)],
            DateTimeOffset.UtcNow, Ct);

        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.campaign_recipients"));
    }

    [Theory]
    [InlineData("Cancelled", "campaign_cancelled")]
    [InlineData("Failed", "campaign_failed")]
    public async Task IfTheCampaignEndedWhileFreezingTheStragglersAreSkippedWithItsTerminalReason(string campaignStatus, string expectedCode)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var id = await LaunchAsync(f, Input(f, [Guid.CreateVersion7()]));
        await SeedPendingRecipientAsync(f, id, "Rezagado", "573009990001");
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.campaigns SET status = @status WHERE id = @id", ("status", campaignStatus), ("id", id));
        using var scope = f.Host.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ICampaignDispatchStore>().MarkFrozenAsync(id, DateTimeOffset.UtcNow, Ct);

        Assert.Equal(("Skipped", expectedCode), (
            await RecipientStatusAsync(f, "Rezagado"),
            await ScalarAsync<string>(f.ConnectionString, "SELECT failure_code FROM messaging.campaign_recipients")));
    }

    [Fact]
    public async Task MarkingTheAudienceFrozenOnACampaignThatIsStillSendingLeavesItsPendingRecipientsAlone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var id = await LaunchAsync(f, Input(f, [Guid.CreateVersion7()]));
        await SeedPendingRecipientAsync(f, id, "Normal", "573009990001");
        using var scope = f.Host.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ICampaignDispatchStore>().MarkFrozenAsync(id, DateTimeOffset.UtcNow, Ct);

        Assert.Equal("Pending", await RecipientStatusAsync(f, "Normal"));
    }

    [Fact]
    public async Task NothingIsClaimedBeforeTheAudienceIsFrozen()
    {
        // A9: otro pod escribe lotes de 500 que ya son Pending mientras congela; reclamar antes dejaría salir a un destinatario que el
        // desduplicado del final todavía puede descartar. Nada se reclama hasta que audience_frozen_at esté puesto.
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var pod = f.Pod;
        var id = await LaunchAsync(f, Input(f, [Guid.CreateVersion7()]));
        await SeedPendingRecipientAsync(f, id, "Lote ya insertado", "573009990001");
        using var scope = f.Host.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<CampaignDispatcher>();

        Assert.Empty(await dispatcher.ClaimAsync(20, Ct));
        Assert.Equal("Pending", await RecipientStatusAsync(f, "Lote ya insertado"));

        await scope.ServiceProvider.GetRequiredService<ICampaignDispatchStore>().MarkFrozenAsync(id, DateTimeOffset.UtcNow, Ct);

        Assert.Equal("Lote ya insertado", Assert.Single(await dispatcher.ClaimAsync(20, Ct)).CustomerName);
    }

    private static Task<int> SeedSendingRecipientAsync(
        Fixture f, Guid campaignId, string name, string waId, Guid? messageId, Guid? conversationId, string lockedUntil, int attempts = 1, Guid? customerId = null) =>
        ExecuteAsync(f.ConnectionString,
            $"""
            INSERT INTO messaging.campaign_recipients (id, campaign_id, tenant_id, customer_id, customer_name, wa_id, status, conversation_id, message_id, attempts, next_attempt_at, locked_until)
            VALUES (@id, @campaign, @tenant, @customer, @name, @wa, 'Sending', @conversation, @message, {attempts}, now() - interval '1 hour', {lockedUntil})
            """,
            ("id", Guid.CreateVersion7()), ("campaign", campaignId), ("tenant", f.Tenant.TenantId), ("customer", customerId ?? Guid.CreateVersion7()), ("name", name), ("wa", waId),
            ("conversation", (object?)conversationId ?? DBNull.Value), ("message", (object?)messageId ?? DBNull.Value));

    private static Task<int> SeedPendingRecipientAsync(Fixture f, Guid campaignId, string name, string waId, Guid? customerId = null) =>
        ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.campaign_recipients (id, campaign_id, tenant_id, customer_id, customer_name, wa_id, status, attempts, next_attempt_at)
            VALUES (@id, @campaign, @tenant, @customer, @name, @wa, 'Pending', 0, now() - interval '1 hour')
            """,
            ("id", Guid.CreateVersion7()), ("campaign", campaignId), ("tenant", f.Tenant.TenantId), ("customer", customerId ?? Guid.CreateVersion7()), ("name", name), ("wa", waId));
}
