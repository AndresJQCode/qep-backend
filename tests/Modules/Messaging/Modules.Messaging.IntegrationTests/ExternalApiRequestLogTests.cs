using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Spec 2026-11-01 parte 1 por HTTP y contra la base real: cada llamada identificada deja una fila, sin nada de lo que mandó el
/// integrador, y quien sólo conoce un prefijo no escribe nada. Las filas se escriben después de responder, así que se esperan sondeando.
/// </summary>
public sealed class ExternalApiRequestLogTests
{
    private const string Phone = "573001234567";
    private const string TemplateName = "aviso_despacho";
    private const string Language = "es_CO";
    private const string ParameterValue = "ValorSoloEnElCuerpo4821";
    private const string CountRowsSql = "SELECT count(*) FROM api_access.api_key_requests WHERE tenant_id = @tenantId";
    private const string RowsSql =
        "SELECT status_code, error_code, message_id, trace_id, duration_ms, api_key_id FROM api_access.api_key_requests WHERE tenant_id = @tenantId ORDER BY occurred_at, id";

    // 43 caracteres de secreto: tiene la forma de una clave, así que cae en Unknown y no en Malformed.
    private const string InventedKey = "qep_abcd1234_inventada" + "0000000000000000000000000000000000";

    private const string Catalog = """
        {"data":[
          {"id":"1","name":"aviso_despacho","language":"es_CO","status":"APPROVED","category":"UTILITY",
           "components":[{"type":"BODY","text":"Hola {{1}}, tu pedido va en camino."}]}
        ]}
        """;

    private const string MetaOk =
        """{"messaging_product":"whatsapp","contacts":[{"input":"573001234567","wa_id":"573001234567"}],"messages":[{"id":"wamid.external"}]}""";

    private static readonly string[] SendScope = [MessagingScopes.TemplatesSend];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(
        QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid MemberId, Guid ConnectionId, Guid KeyId, string Plaintext, HttpClient Client);

    private sealed record Row(int StatusCode, string? ErrorCode, Guid? MessageId, string? TraceId, int DurationMs, Guid ApiKeyId);

    /// <summary>La respuesta de Meta va acá porque <see cref="FakeMetaGraphHandler"/> se queda con la primera regla que coincide:
    /// una segunda regla sobre la misma ruta nunca se aplica.</summary>
    private static async Task<Fixture> ArrangeAsync(TestDatabase database, HttpStatusCode metaStatus = HttpStatusCode.OK, string metaBody = MetaOk)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableDeveloperAsync(connectionString, tenant.TenantId);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var member = await OwnerMembershipIdAsync(connectionString, tenant);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var (keyId, plaintext) = await SeedApiKeyAsync(connectionString, tenant.TenantId, member, SendScope);
        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        factory.MetaHandler.Respond("/111/messages$", metaStatus, metaBody);
        return new Fixture(factory, connectionString, tenant, member, connectionId, keyId, plaintext, CreateExternalClient(factory, plaintext));
    }

    private static object Body(Guid idempotencyKey, Guid connectionId) => new
    {
        idempotencyKey,
        connectionId,
        to = Phone,
        template = new { name = TemplateName, language = Language, parameters = new Dictionary<string, string> { ["1"] = ParameterValue } },
    };

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, object body) => client.PostAsJsonAsync(ExternalMessagesUrl, body, Ct);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    /// <summary>La fila se escribe en <c>OnCompleted</c>: el cliente puede tener la respuesta antes de que exista.</summary>
    private static async Task WaitForRowsAsync(string connectionString, Guid tenantId, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await CountAsync(connectionString, CountRowsSql, ("tenantId", tenantId)) >= expected)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail($"No aparecieron {expected} filas en api_access.api_key_requests.");
    }

    private static async Task<List<Row>> RowsAsync(Fixture f, int expected)
    {
        await WaitForRowsAsync(f.ConnectionString, f.Tenant.TenantId, expected);
        await using var connection = new Npgsql.NpgsqlConnection(f.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(RowsSql, connection);
        command.Parameters.AddWithValue("tenantId", f.Tenant.TenantId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<Row>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new Row(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetGuid(5)));
        }

        return rows;
    }

    [Fact]
    public async Task AnAcceptedSendWritesOneRowWithItsMessage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await PostAsync(f.Client, Body(Guid.CreateVersion7(), f.ConnectionId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var messageId = (await BodyAsync(response)).GetProperty("messageId").GetGuid();
        var row = Assert.Single(await RowsAsync(f, 1));
        Assert.Equal(201, row.StatusCode);
        Assert.Equal(messageId, row.MessageId);
        Assert.Equal(f.KeyId, row.ApiKeyId);
        Assert.Null(row.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(row.TraceId));
        Assert.True(row.DurationMs >= 0);
    }

    [Fact]
    public async Task TheRowCarriesNoneOfTheCallData()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var idempotencyKey = Guid.CreateVersion7();

        // Con query string a propósito: la fila guarda sólo la ruta.
        var response = await f.Client.PostAsJsonAsync($"{ExternalMessagesUrl}?marca=SoloEnLaQuery", Body(idempotencyKey, f.ConnectionId), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await WaitForRowsAsync(f.ConnectionString, f.Tenant.TenantId, 1);
        var json = await ScalarAsync<string>(
            f.ConnectionString,
            "SELECT row_to_json(r)::text FROM api_access.api_key_requests r WHERE tenant_id = @tenantId",
            ("tenantId", f.Tenant.TenantId));
        Assert.DoesNotContain(f.Plaintext, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Phone, json, StringComparison.Ordinal);
        Assert.DoesNotContain(ParameterValue, json, StringComparison.Ordinal);
        Assert.DoesNotContain(idempotencyKey.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SoloEnLaQuery", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnconfirmedRepeatKeepsTheMessageOnTheRow()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, HttpStatusCode.InternalServerError, "{}");
        using var host = f.Factory;
        var body = Body(Guid.CreateVersion7(), f.ConnectionId);

        Assert.Equal(HttpStatusCode.BadGateway, (await PostAsync(f.Client, body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, (await PostAsync(f.Client, body)).StatusCode);

        var rows = await RowsAsync(f, 2);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(502, row.StatusCode);
            Assert.Equal("external.send_unconfirmed", row.ErrorCode);
            Assert.NotNull(row.MessageId);
        });
        Assert.Equal(rows[0].MessageId, rows[1].MessageId);
    }

    [Fact]
    public async Task AValidationFailureHasItsCodeAndNoMessage()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await PostAsync(f.Client, new { connectionId = f.ConnectionId, to = Phone });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var row = Assert.Single(await RowsAsync(f, 1));
        Assert.Equal(422, row.StatusCode);
        Assert.Equal("validation.failed", row.ErrorCode);
        Assert.Null(row.MessageId);
    }

    [Fact]
    public async Task AMissingScopeIsRecorded()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var (keyId, plaintext) = await SeedApiKeyAsync(f.ConnectionString, f.Tenant.TenantId, f.MemberId, ["other.scope"]);
        using var scopeless = CreateExternalClient(f.Factory, plaintext);

        var response = await scopeless.GetAsync($"{ExternalMessagesUrl}/{Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var row = Assert.Single(await RowsAsync(f, 1));
        Assert.Equal(403, row.StatusCode);
        Assert.Equal("external.auth.scope_missing", row.ErrorCode);
        Assert.Equal(keyId, row.ApiKeyId);
    }

    [Fact]
    public async Task ARevokedKeyIsRecordedWithItsTenant()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await ExecuteAsync(
            f.ConnectionString,
            "UPDATE api_access.api_keys SET revoked_at = now(), revoked_by_member_id = @member WHERE id = @id",
            ("member", f.MemberId), ("id", f.KeyId));

        var response = await f.Client.GetAsync(ExternalConnectionsUrl, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var row = Assert.Single(await RowsAsync(f, 1));
        Assert.Equal(401, row.StatusCode);
        Assert.Equal("external.auth.invalid_key", row.ErrorCode);
        Assert.Equal(f.KeyId, row.ApiKeyId);
    }

    [Fact]
    public async Task AnExpiredKeyIsRecordedWithItsTenant()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await ExecuteAsync(
            f.ConnectionString,
            "UPDATE api_access.api_keys SET expires_at = now() - interval '1 hour' WHERE id = @id",
            ("id", f.KeyId));

        var response = await f.Client.GetAsync(ExternalConnectionsUrl, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var row = Assert.Single(await RowsAsync(f, 1));
        Assert.Equal(401, row.StatusCode);
        Assert.Equal("external.auth.invalid_key", row.ErrorCode);
        Assert.Equal(f.KeyId, row.ApiKeyId);
    }

    [Fact]
    public async Task AWrongSecretAndAnInventedKeyWriteNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var wrongSecret = f.Plaintext[..13] + new string('A', 43);

        foreach (var key in new[] { wrongSecret, InventedKey })
        {
            using var client = CreateExternalClient(f.Factory, key);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ExternalConnectionsUrl, Ct)).StatusCode);
        }

        // Una llamada buena de testigo: contar cero sin ella pasaría también si el registro todavía no hubiera escrito.
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync(ExternalConnectionsUrl, Ct)).StatusCode);

        var row = Assert.Single(await RowsAsync(f, 1));
        Assert.Equal(200, row.StatusCode);
    }

    [Fact]
    public async Task AThrowingLogDoesNotBreakTheSend()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var failing = f.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IApiKeyRequestLog>();
            services.AddScoped<IApiKeyRequestLog, ThrowingLog>();
        }));
        using var client = CreateExternalClient(failing, f.Plaintext);

        var response = await PostAsync(client, Body(Guid.CreateVersion7(), f.ConnectionId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task TheRowTraceIdIsTheOneTheClientGot()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await PostAsync(f.Client, new { connectionId = f.ConnectionId, to = Phone });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var traceId = (await BodyAsync(response)).GetProperty("traceId").GetString();
        var row = Assert.Single(await RowsAsync(f, 1));
        Assert.Equal(traceId, row.TraceId);
    }

    private sealed class ThrowingLog : IApiKeyRequestLog
    {
        public Task RecordAsync(ApiKeyRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("El registro falló a propósito.");
    }
}
