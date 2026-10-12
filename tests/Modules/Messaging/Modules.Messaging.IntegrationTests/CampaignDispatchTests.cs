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
          {"name":"promo","language":"es","status":"APPROVED","category":"MARKETING",
           "components":[{"type":"BODY","text":"Hola {{1}}, tu código es {{2}}"}]},
          {"name":"aviso","language":"es","status":"APPROVED","category":"UTILITY",
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
        await SeedSendingRecipientAsync(f, id, "Sin mensaje", "573009990002", messageId: null, conversationId: null, lockedUntil: "now() - interval '1 hour'");
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
        for (var index = 0; index < recipients; index++)
        {
            await SeedPendingRecipientAsync(f, id, $"Cliente {index:D3}", $"5730099{index:D5}");
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

    private static Task<int> SeedSendingRecipientAsync(Fixture f, Guid campaignId, string name, string waId, Guid? messageId, Guid? conversationId, string lockedUntil) =>
        ExecuteAsync(f.ConnectionString,
            $"""
            INSERT INTO messaging.campaign_recipients (id, campaign_id, tenant_id, customer_id, customer_name, wa_id, status, conversation_id, message_id, attempts, next_attempt_at, locked_until)
            VALUES (@id, @campaign, @tenant, @customer, @name, @wa, 'Sending', @conversation, @message, 1, now() - interval '1 hour', {lockedUntil})
            """,
            ("id", Guid.CreateVersion7()), ("campaign", campaignId), ("tenant", f.Tenant.TenantId), ("customer", Guid.CreateVersion7()), ("name", name), ("wa", waId),
            ("conversation", (object?)conversationId ?? DBNull.Value), ("message", (object?)messageId ?? DBNull.Value));

    private static Task<int> SeedPendingRecipientAsync(Fixture f, Guid campaignId, string name, string waId) =>
        ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.campaign_recipients (id, campaign_id, tenant_id, customer_id, customer_name, wa_id, status, attempts, next_attempt_at)
            VALUES (@id, @campaign, @tenant, @customer, @name, @wa, 'Pending', 0, now() - interval '1 hour')
            """,
            ("id", Guid.CreateVersion7()), ("campaign", campaignId), ("tenant", f.Tenant.TenantId), ("customer", Guid.CreateVersion7()), ("name", name), ("wa", waId));
}
