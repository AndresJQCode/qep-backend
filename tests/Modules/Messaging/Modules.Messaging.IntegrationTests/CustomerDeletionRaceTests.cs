using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Infrastructure;
using Npgsql;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Borrar clientes (spec 2026-11-10, A-12; plan P4): un entrante, un eco o un cambio de cliente que llega a mitad del borrado no
/// puede dejar una conversación colgando de un cliente que ya no existe. El borrado se simula con el candado exclusivo del cliente
/// tomado a mano: la escritura de Mensajería tiene que esperar, y al despertar ver el cliente ya borrado. El webhook queda para
/// reintento, y el reintento crea un cliente nuevo (D-4).
/// </summary>
public sealed class CustomerDeletionRaceTests
{
    private const string PhoneNumberId = "111";
    private const string WaId = "573001112233";
    private const string WarmUpWaId = "573009998877";
    private static readonly TimeSpan NoAnswerWindow = TimeSpan.FromMilliseconds(500);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Webhook)
        : IDisposable
    {
        public void Dispose()
        {
            Webhook.Dispose();
            Factory.Dispose();
        }
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", PhoneNumberId, "222");
        return new Fixture(factory, connectionString, tenant, connectionId, factory.CreateClient());
    }

    private static async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> HoldExclusiveAsync(
        string connectionString, Guid tenantId, Guid customerId)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", connection, transaction);
        command.Parameters.AddWithValue("key", CustomerReferenceLock.KeyFor(tenantId, customerId));
        await command.ExecuteNonQueryAsync(Ct);
        return (connection, transaction);
    }

    /// <summary>Lo que hace el borrado con el cliente y su conversación (D-5), en la transacción que tiene el candado exclusivo.</summary>
    private static async Task DeleteAndCommitAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid customerId, Guid? conversationId)
    {
        if (conversationId is { } conversation)
        {
            await using var messages = new NpgsqlCommand("DELETE FROM messaging.messages WHERE conversation_id = @id", connection, transaction);
            messages.Parameters.AddWithValue("id", conversation);
            await messages.ExecuteNonQueryAsync(Ct);
            await using var row = new NpgsqlCommand("DELETE FROM messaging.conversations WHERE id = @id", connection, transaction);
            row.Parameters.AddWithValue("id", conversation);
            Assert.Equal(1, await row.ExecuteNonQueryAsync(Ct));
        }

        await using (var customer = new NpgsqlCommand("DELETE FROM customers.customers WHERE id = @id", connection, transaction))
        {
            customer.Parameters.AddWithValue("id", customerId);
            Assert.Equal(1, await customer.ExecuteNonQueryAsync(Ct));
        }

        await transaction.CommitAsync(Ct);
    }

    private static async Task PostAndDrainAsync(Fixture f, string json)
    {
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(f.Webhook, json)).StatusCode);
        await DrainDeliveriesAsync(f.Factory);
    }

    /// <summary>Lo que quedó para reintento vuelve a estar disponible ya, sin esperar el lease.</summary>
    private static async Task RetryNowAsync(Fixture f)
    {
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.webhook_deliveries SET claimed_until = NULL WHERE processed_at IS NULL");
        await DrainDeliveriesAsync(f.Factory);
    }

    private static async Task AssertLandedOnANewCustomerAsync(Fixture f, string wamid, Guid deleted)
    {
        var owner = await ScalarAsync<object>(f.ConnectionString,
            """
            SELECT c.customer_id FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id
             WHERE m.wamid = @wamid
            """, ("wamid", wamid));
        var customerId = Assert.IsType<Guid>(owner);
        Assert.NotEqual(deleted, customerId);
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM customers.customers WHERE tenant_id = @tenantId AND id = @id", ("tenantId", f.Tenant.TenantId), ("id", customerId)));
        Assert.Equal(0L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.conversations WHERE customer_id = @id", ("id", deleted)));
    }

    [Fact]
    public async Task AnInboundDuringDeletionLandsOnANewCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var customerId = await CreateCustomerAsync(f.Factory, f.Tenant, "LAURA GÓMEZ", "300 111 2233");
        var conversationId = await SeedBsuidConversationAsync(
            f.Factory, f.ConnectionString, f.Tenant.TenantId, f.ConnectionId, MetaPayloads.UserIdFor(WaId), WaId);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET customer_id = @customerId WHERE id = @id",
            ("customerId", customerId), ("id", conversationId));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Calentamiento: la primera ingesta de un host nuevo tarda más que la ventana de espera, y sin él la prueba pasaría aunque
        // el candado no existiera (la ingesta "esperaría" por arranque en frío, no por el candado).
        await PostAndDrainAsync(f, MetaPayloads.Inbound(PhoneNumberId, MetaPayloads.UserIdFor(WarmUpWaId), WarmUpWaId, "wamid.warmup", now - 120, "Hola"));
        var (connection, transaction) = await HoldExclusiveAsync(f.ConnectionString, f.Tenant.TenantId, customerId);
        await using var _c = connection;
        await using var _t = transaction;
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(
            f.Webhook, MetaPayloads.Inbound(PhoneNumberId, MetaPayloads.UserIdFor(WaId), WaId, "wamid.race", now - 60, "¿Siguen abiertos?"))).StatusCode);

        var drain = Task.Run(() => DrainDeliveriesAsync(f.Factory), Ct);

        Assert.NotSame(drain, await Task.WhenAny(drain, Task.Delay(NoAnswerWindow, Ct)));
        await DeleteAndCommitAsync(connection, transaction, customerId, conversationId);
        await drain;

        // La transacción de la ingesta se deshizo entera y la entrega queda para reintento.
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE wamid = 'wamid.race'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NULL"));

        await RetryNowAsync(f);

        await AssertLandedOnANewCustomerAsync(f, "wamid.race", customerId);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NULL"));
    }

    [Fact]
    public async Task ACoexistenceEchoDuringDeletionLandsOnANewCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var customerId = await CreateCustomerAsync(f.Factory, f.Tenant, "LAURA GÓMEZ", "300 111 2233");
        var conversationId = await SeedConversationAsync(f.Factory, f.Tenant.TenantId, f.ConnectionId, WaId);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET customer_id = @customerId WHERE id = @id",
            ("customerId", customerId), ("id", conversationId));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await PostAndDrainAsync(f, MetaPayloads.SmbEchoes(PhoneNumberId, WarmUpWaId, "wamid.echo.warmup", "Hola", now - 120));
        var (connection, transaction) = await HoldExclusiveAsync(f.ConnectionString, f.Tenant.TenantId, customerId);
        await using var _c = connection;
        await using var _t = transaction;
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(
            f.Webhook, MetaPayloads.SmbEchoes(PhoneNumberId, WaId, "wamid.echo.race", "Respondido desde el celular", now - 60))).StatusCode);

        var drain = Task.Run(() => DrainDeliveriesAsync(f.Factory), Ct);

        Assert.NotSame(drain, await Task.WhenAny(drain, Task.Delay(NoAnswerWindow, Ct)));
        await DeleteAndCommitAsync(connection, transaction, customerId, conversationId);
        await drain;

        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE wamid = 'wamid.echo.race'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NULL"));

        await RetryNowAsync(f);

        await AssertLandedOnANewCustomerAsync(f, "wamid.echo.race", customerId);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NULL"));
    }

    [Fact]
    public async Task ChangingTheConversationCustomerToADeletedOneFails()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var customers = await CreateCustomersAsync(f.Factory, f.Tenant, ("LAURA GÓMEZ", "300 111 2233"), ("ZIBA TIENDA", "301 222 3344"));
        var (current, target) = (customers[0], customers[1]);
        var conversationId = await SeedBsuidConversationAsync(
            f.Factory, f.ConnectionString, f.Tenant.TenantId, f.ConnectionId, MetaPayloads.UserIdFor(WaId), WaId);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET customer_id = @customerId WHERE id = @id",
            ("customerId", current), ("id", conversationId));
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);
        // Calentamiento, y la versión para el If-Match.
        var detail = await (await client.GetAsync(ConversationUrl(f.Tenant.TenantId, conversationId), Ct))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>(Ct);
        var version = detail.GetProperty("version").GetInt64();
        // El GET no recorre el camino del cambio: sin esta vuelta en vacío (el mismo cliente responde 200 sin versión nueva), el PUT de
        // abajo "esperaría" por arranque en frío aunque el candado no existiera.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"{ConversationUrl(f.Tenant.TenantId, conversationId)}/customer",
            new { customerId = current }, $"\"{version}\"")).StatusCode);
        var (connection, transaction) = await HoldExclusiveAsync(f.ConnectionString, f.Tenant.TenantId, target);
        await using var _c = connection;
        await using var _t = transaction;

        var put = SendAsync(client, HttpMethod.Put, $"{ConversationUrl(f.Tenant.TenantId, conversationId)}/customer",
            new { customerId = target }, $"\"{version}\"");

        Assert.NotSame(put, await Task.WhenAny(put, Task.Delay(NoAnswerWindow, Ct)));
        await DeleteAndCommitAsync(connection, transaction, target, conversationId: null);

        var response = await put;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.conversation.customer_not_found", (await ProblemAsync(response)).Code);
        Assert.Equal(current, await ScalarAsync<Guid>(f.ConnectionString,
            "SELECT customer_id FROM messaging.conversations WHERE id = @id", ("id", conversationId)));
        Assert.Equal(version, await ScalarAsync<long>(f.ConnectionString,
            "SELECT version FROM messaging.conversations WHERE id = @id", ("id", conversationId)));
    }

    /// <summary>
    /// El orden de candados frente al borrado (P1 y P5): el borrado toma el exclusivo del cliente y después bloquea sus conversaciones
    /// con <c>FOR UPDATE</c>. La adopción de una fila vieja sin BSUID (las que abre <c>OpenByPhoneAsync</c> con el cliente de una
    /// cotización en el número de otro, D16b) no puede tomar la fila antes que el candado del cliente: el <c>FOR UPDATE</c> del borrado
    /// quedaría esperando a la ingesta mientras la ingesta espera el candado, y Postgres abortaría a uno de los dos. Con el orden bueno,
    /// la ingesta espera el candado sin tener la fila y el <c>FOR UPDATE</c> entra en el acto.
    /// </summary>
    [Fact]
    public async Task AdoptingALegacyConversationDuringDeletionWaitsWithoutHoldingItsRow()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        // El cliente de la conversación vieja tiene otro número: el entrante resuelve (y crea) otro cliente por su teléfono.
        var owner = await CreateCustomerAsync(f.Factory, f.Tenant, "CONSTRUCTORA SAS", "300 555 0101");
        var conversationId = await SeedConversationAsync(f.Factory, f.Tenant.TenantId, f.ConnectionId, WaId);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET customer_id = @customerId WHERE id = @id",
            ("customerId", owner), ("id", conversationId));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await PostAndDrainAsync(f, MetaPayloads.Inbound(PhoneNumberId, MetaPayloads.UserIdFor(WarmUpWaId), WarmUpWaId, "wamid.warmup", now - 120, "Hola"));
        var (connection, transaction) = await HoldExclusiveAsync(f.ConnectionString, f.Tenant.TenantId, owner);
        await using var _c = connection;
        await using var _t = transaction;
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(
            f.Webhook, MetaPayloads.Inbound(PhoneNumberId, MetaPayloads.UserIdFor(WaId), WaId, "wamid.adopt", now - 60, "Hola, soy de compras"))).StatusCode);

        var drain = Task.Run(() => DrainDeliveriesAsync(f.Factory), Ct);

        Assert.NotSame(drain, await Task.WhenAny(drain, Task.Delay(NoAnswerWindow, Ct)));
        // P5: el borrado bloquea las conversaciones del cliente. Por debajo del deadlock_timeout (1 s) de Postgres: con la fila tomada
        // por la ingesta esto esperaría hasta que el detector abortara a alguien.
        var clock = Stopwatch.StartNew();
        await using (var lockRows = new NpgsqlCommand(
            "SELECT id FROM messaging.conversations WHERE tenant_id = @tenantId AND customer_id = @id FOR UPDATE", connection, transaction))
        {
            lockRows.Parameters.AddWithValue("tenantId", f.Tenant.TenantId);
            lockRows.Parameters.AddWithValue("id", owner);
            await lockRows.ExecuteNonQueryAsync(Ct);
        }

        Assert.True(clock.Elapsed < NoAnswerWindow, $"FOR UPDATE took {clock.Elapsed}: the ingest held the conversation row.");
        await DeleteAndCommitAsync(connection, transaction, owner, conversationId);
        await drain;

        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE wamid = 'wamid.adopt'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NULL"));

        await RetryNowAsync(f);

        await AssertLandedOnANewCustomerAsync(f, "wamid.adopt", owner);
    }

    private const string CampaignCatalog = """
        {"data":[
          {"id":"1","name":"promo","language":"es","status":"APPROVED","category":"MARKETING",
           "components":[{"type":"BODY","text":"Hola {{1}}"}]}
        ]}
        """;

    /// <summary>P4: el cliente de un destinatario se borra mientras la campaña le abre la conversación. El destinatario termina
    /// <c>Skipped</c>/<c>unreachable</c>, como uno que ya no tiene cliente, y no queda conversación.</summary>
    [Fact]
    public async Task ACampaignRecipientWhoseCustomerIsDeletedWhileOpeningIsSkippedAsUnreachable()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var customerId = await CreateCustomerAsync(f.Factory, f.Tenant, "LAURA GÓMEZ", "300 111 2233");
        f.Factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, CampaignCatalog);
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, CampaignPermissions);
        var url = $"/api/v1/tenants/{f.Tenant.TenantId}/messaging/campaigns";
        var created = await SendAsync(client, HttpMethod.Post, url, new
        {
            name = "Promo",
            connectionId = f.ConnectionId,
            template = new { name = "promo", language = "es" },
            parameters = new Dictionary<string, object> { ["1"] = new { source = "customerName" } },
            audience = new { tagIds = Array.Empty<Guid>(), customerIds = new[] { customerId } },
            scheduledAt = (string?)null,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var campaignId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, $"{url}/{campaignId}/launch", new { expectedVersion = 1 })).StatusCode);
        // La audiencia queda congelada a mano con el único destinatario, para que el worker no la vuelva a congelar.
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.campaigns SET audience_frozen_at = now() WHERE id = @id", ("id", campaignId));
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.campaign_recipients (id, campaign_id, tenant_id, customer_id, customer_name, wa_id, status, attempts, next_attempt_at)
            VALUES (@id, @campaign, @tenant, @customer, 'Laura', @wa, 'Pending', 0, now() - interval '1 hour')
            """,
            ("id", Guid.CreateVersion7()), ("campaign", campaignId), ("tenant", f.Tenant.TenantId), ("customer", customerId), ("wa", WaId));
        var (connection, transaction) = await HoldExclusiveAsync(f.ConnectionString, f.Tenant.TenantId, customerId);
        await using var _c = connection;
        await using var _t = transaction;

        var drain = Task.Run(() => DrainCampaignsAsync(f.Factory), Ct);

        Assert.NotSame(drain, await Task.WhenAny(drain, Task.Delay(NoAnswerWindow, Ct)));
        await DeleteAndCommitAsync(connection, transaction, customerId, conversationId: null);
        await drain;

        Assert.Equal("Skipped|unreachable", await ScalarAsync<string>(f.ConnectionString,
            "SELECT status || '|' || coalesce(failure_code, '') FROM messaging.campaign_recipients WHERE campaign_id = @id", ("id", campaignId)));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));
    }
}
