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
}
