using System.Net;
using System.Net.Http.Json;
using Modules.Integrations.Application;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Spec 2026-10-08, ciclo de vida por HTTP: If-Match 428/412 donde el spec lo pide, <c>test</c> sin
/// If-Match y siempre 200 (P10, P27), eventos y auditoría, cascada de secretos, el PUT que conserva el
/// secreto y el 503 sin llave activa.
/// </summary>
public sealed class ConnectionLifecycleApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ConnectionResponse> ReadAsync(HttpResponseMessage response)
    {
        var connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        Assert.NotNull(connection);
        return connection;
    }

    private static Task<long> EventsAsync(string connectionString, string eventName, Guid connectionId) =>
        ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = @eventName AND payload->>'connectionId' = @id",
            ("eventName", eventName),
            ("id", connectionId.ToString()));

    [Fact]
    public async Task PauseResumeTestAndDeleteFollowTheirContract()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await CreateConnectionAsync(client, tenant.TenantId);
        var url = ConnectionUrl(tenant.TenantId, created.Id);

        var pauseWithoutIfMatch = await SendAsync(client, HttpMethod.Post, $"{url}/pause");
        var pauseStale = await SendAsync(client, HttpMethod.Post, $"{url}/pause", ifMatch: "\"7\"");
        var paused = await SendAsync(client, HttpMethod.Post, $"{url}/pause", ifMatch: "\"1\"");
        var pausedAgain = await SendAsync(client, HttpMethod.Post, $"{url}/pause", ifMatch: "\"2\"");

        Assert.Equal(HttpStatusCode.PreconditionRequired, pauseWithoutIfMatch.StatusCode);
        Assert.Equal("precondition.if_match_required", (await ProblemAsync(pauseWithoutIfMatch)).Code);
        Assert.Equal(HttpStatusCode.PreconditionFailed, pauseStale.StatusCode);
        Assert.Equal("concurrency.conflict", (await ProblemAsync(pauseStale)).Code);
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        Assert.Equal("Paused", (await ReadAsync(paused)).Status);
        Assert.Equal("integrations.connection.not_active", (await ProblemAsync(pausedAgain)).Code);

        // D4: reanudar vuelve a probar; con la credencial rechazada queda NeedsAttention y responde 422.
        factory.ZenviaHandler.Status = HttpStatusCode.Unauthorized;
        var resumed = await SendAsync(client, HttpMethod.Post, $"{url}/resume", ifMatch: "\"2\"");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resumed.StatusCode);
        var (resumeCode, resumeKeys) = await ProblemAsync(resumed);
        Assert.Equal("integrations.connection.credentials_rejected", resumeCode);
        Assert.Equal(["secrets.apiToken"], resumeKeys);
        var attention = await client.GetFromJsonAsync<ConnectionResponse>(url, Ct);
        Assert.Equal("NeedsAttention", attention!.Status);
        Assert.Equal(3L, attention.Version);
        Assert.Equal("credentials_rejected", attention.LastFailureCode);

        // NeedsAttention → Active con un test que pasa (sin If-Match, P27).
        factory.ZenviaHandler.Status = HttpStatusCode.OK;
        var tested = await SendAsync(client, HttpMethod.Post, $"{url}/test");
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        var active = await ReadAsync(tested);
        Assert.Equal("Active", active.Status);
        Assert.Equal(4L, active.Version);

        var deleteWithoutIfMatch = await SendAsync(client, HttpMethod.Delete, url);
        var deleteStale = await SendAsync(client, HttpMethod.Delete, url, ifMatch: "\"3\"");
        var deleted = await SendAsync(client, HttpMethod.Delete, url, ifMatch: "\"4\"");
        var gone = await SendAsync(client, HttpMethod.Get, url);

        Assert.Equal(HttpStatusCode.PreconditionRequired, deleteWithoutIfMatch.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, deleteStale.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("integrations.connection.not_found", (await ProblemAsync(gone)).Code);
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM integrations.connection_secrets WHERE connection_id = @id", ("id", created.Id)));

        Assert.Equal(1L, await EventsAsync(connectionString, ConnectionEvents.Paused, created.Id));
        Assert.Equal(1L, await EventsAsync(connectionString, ConnectionEvents.NeedsAttention, created.Id));
        Assert.Equal(1L, await EventsAsync(connectionString, ConnectionEvents.Deleted, created.Id));
        var actions = await ScalarAsync<string>(
            connectionString,
            "SELECT string_agg(action, ',') FROM audit.entries WHERE resource_type = 'integration_connection' AND resource_id = @id",
            ("id", created.Id.ToString()));
        foreach (var action in new[] { "created", "paused", "needs_attention", "verified", "deleted" })
        {
            Assert.Contains($"integrations.connection.{action}", actions, StringComparison.Ordinal);
        }
    }

    // F4 del plan de frontend y P10: una prueba fallida responde 200 con la falla anotada. Unreachable
    // deja el estado; un rechazo sobre una Active la pasa a NeedsAttention con su evento (DECISIÓN 2,
    // owner 2026-10-08). Sin If-Match (F11 / P27).
    [Fact]
    public async Task AFailedTestAnswers200WithTheFailureAndNeedsNoIfMatch()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await CreateConnectionAsync(client, tenant.TenantId);
        var url = ConnectionUrl(tenant.TenantId, created.Id);

        factory.ZenviaHandler.Throw = new HttpRequestException("connection refused");
        var unreachable = await SendAsync(client, HttpMethod.Post, $"{url}/test");
        factory.ZenviaHandler.Throw = null;
        factory.ZenviaHandler.Status = HttpStatusCode.Unauthorized;
        var rejected = await SendAsync(client, HttpMethod.Post, $"{url}/test");

        Assert.Equal(HttpStatusCode.OK, unreachable.StatusCode);
        var afterUnreachable = await ReadAsync(unreachable);
        Assert.Equal("Active", afterUnreachable.Status);
        Assert.Equal("provider_unreachable", afterUnreachable.LastFailureCode);
        Assert.Equal(2L, afterUnreachable.Version);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        var afterRejected = await ReadAsync(rejected);
        Assert.Equal("NeedsAttention", afterRejected.Status);
        Assert.Equal("credentials_rejected", afterRejected.LastFailureCode);
        Assert.NotNull(afterRejected.LastFailureAt);
        Assert.Equal(2L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'integrations.connection.verified' AND outcome = 'failure' AND resource_id = @id",
            ("id", created.Id.ToString())));
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'integrations.connection-needs-attention.v1'"));
    }

    // D5, P9 y Review Focus 3.
    [Fact]
    public async Task PutKeepsAnAbsentOrBlankSecretAndOnlyTestsWhenSomethingThatMattersChanged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await CreateConnectionAsync(client, tenant.TenantId);
        var url = ConnectionUrl(tenant.TenantId, created.Id);

        var renamed = await SendAsync(client, HttpMethod.Put, url, new
        {
            name = "Sede sur",
            fields = new Dictionary<string, string?> { ["fromNumber"] = FromNumber },
            secrets = new Dictionary<string, string?>(),
        }, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(2L, (await ReadAsync(renamed)).Version);
        Assert.Single(factory.ZenviaHandler.Requests);

        var newNumber = await SendAsync(client, HttpMethod.Put, url, new
        {
            name = "Sede sur",
            fields = new Dictionary<string, string?> { ["fromNumber"] = "573009999999" },
            secrets = new Dictionary<string, string?> { ["apiToken"] = "   " },
        }, "\"2\"");
        Assert.Equal(HttpStatusCode.OK, newNumber.StatusCode);
        Assert.Equal(3L, (await ReadAsync(newNumber)).Version);
        Assert.Equal(2, factory.ZenviaHandler.Requests.Count);
        Assert.Equal(SentinelApiToken, factory.ZenviaHandler.Requests.Last().Token);

        var withoutIfMatch = await SendAsync(client, HttpMethod.Put, url, new
        {
            name = "Sede sur",
            fields = new Dictionary<string, string?> { ["fromNumber"] = FromNumber },
            secrets = new Dictionary<string, string?>(),
        });
        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutIfMatch.StatusCode);

        factory.ZenviaHandler.Status = HttpStatusCode.Unauthorized;
        var rejected = await SendAsync(client, HttpMethod.Put, url, new
        {
            name = "Sede sur",
            fields = new Dictionary<string, string?> { ["fromNumber"] = "573009999999" },
            secrets = new Dictionary<string, string?> { ["apiToken"] = "otro-token" },
        }, "\"3\"");
        Assert.Equal("integrations.connection.credentials_rejected", (await ProblemAsync(rejected)).Code);
        var unchanged = await client.GetFromJsonAsync<ConnectionResponse>(url, Ct);
        Assert.Equal(3L, unchanged!.Version);
        Assert.Equal("573009999999", unchanged.Fields["fromNumber"]);
    }

    // F3 del plan de frontend: 503 sin llave activa; la lectura y lo que no cifra siguen.
    [Fact]
    public async Task WithoutAnActiveKeyWritesAre503AndReadsStillWork()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var created = await CreateConnectionAsync(client, tenant.TenantId);
        var url = ConnectionUrl(tenant.TenantId, created.Id);
        using var withoutKey = factory.WithSecretProtection(string.Empty);
        using var degraded = CreateClient(withoutKey, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);

        var create = await SendAsync(degraded, HttpMethod.Post, ConnectionsUrl(tenant.TenantId), ZenviaBody("Otra"));
        var update = await SendAsync(degraded, HttpMethod.Put, url, new
        {
            name = "Sede sur",
            fields = new Dictionary<string, string?> { ["fromNumber"] = FromNumber },
            secrets = new Dictionary<string, string?>(),
        }, "\"1\"");
        var read = await degraded.GetFromJsonAsync<ConnectionResponse>(url, Ct);
        var pause = await SendAsync(degraded, HttpMethod.Post, $"{url}/pause", ifMatch: "\"1\"");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, create.StatusCode);
        Assert.Equal("integrations.secret_protection.unavailable", (await ProblemAsync(create)).Code);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, update.StatusCode);
        Assert.True(read!.Secrets["apiToken"].Readable);
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
    }
}
