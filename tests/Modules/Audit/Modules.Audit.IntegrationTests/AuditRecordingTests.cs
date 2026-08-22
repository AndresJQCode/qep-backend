using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Audit.IntegrationTests;

public sealed class AuditRecordingTests
{
    private const string SeededTenantId = "01900000-0000-7000-8000-000000000001";
    private const string AdminSubjectId = "01900000-0000-7000-8000-000000000002";
    private const string AuditEventName = "platform.audit.recorded.v1";
    private static readonly string[] DefaultRoles = ["advisor"];

    // Camino atómico (ADR 0019): una acción de Tenancy escribe su entrada de auditoría en la
    // tabla audit.entries (de Audit) de forma síncrona, en la misma transacción que el cambio.
    [Fact]
    public async Task InviteWritesAuditEntryAtomicallyToAuditStore()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdminClient(factory);
        var email = NewEmail();

        var response = await InviteAsync(client, email);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(membership);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // La fila de auditoría está presente inmediatamente después del request (no es eventual).
        var row = await QueryRowAsync(
            connection,
            """
            SELECT actor_id::text, actor_type, outcome, source
            FROM audit.entries
            WHERE resource_id = @resourceId AND action = 'tenancy.membership.invited'
            """,
            ("resourceId", membership!.Id.ToString()));
        Assert.NotNull(row);
        Assert.Equal(AdminSubjectId, row![0]);
        Assert.Equal("Human", row[1]);
        Assert.Equal("success", row[2]);
        Assert.Equal("tenancy", row[3]);
    }

    // Camino operativo (ADR 0019): un evento de auditoría del Outbox de plataforma lo proyecta
    // el worker de fondo a audit.entries, exactamente una vez (idempotente por el inbox de Audit).
    [Fact]
    public async Task OperationalAuditEventIsProjectedExactlyOnce()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // Construir el cliente de la factory arranca el host: corren las migraciones y el worker.
        using var client = factory.CreateClient();

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var resourceId = Guid.NewGuid().ToString();
        var occurredAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var payload = $$"""
            {"tenantId":"{{SeededTenantId}}","actorId":"{{AdminSubjectId}}","actorType":"System","action":"tenancy.tenant.decommissioned","resourceType":"tenant","resourceId":"{{resourceId}}","outcome":"success","changedFields":[],"source":"tenancy","occurredAt":"{{occurredAt}}"}
            """;
        await SeedAuditOutboxAsync(connection, payload);

        var count = await WaitForAuditCountAsync(connection, resourceId, expected: 1);
        Assert.Equal(1L, count);

        // Los ticks de sondeo posteriores no proyectan un duplicado.
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Equal(1L, await CountAuditAsync(connection, resourceId));
    }

    private static async Task SeedAuditOutboxAsync(NpgsqlConnection connection, string payload)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.outbox_messages
                (id, event_name, payload, correlation_id, occurred_at, attempts)
            VALUES (@id, @eventName, @payload::jsonb, @correlationId, @occurredAt, 0)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("eventName", AuditEventName);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("correlationId", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("occurredAt", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> WaitForAuditCountAsync(
        NpgsqlConnection connection,
        string resourceId,
        long expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var count = await CountAuditAsync(connection, resourceId);
            if (count >= expected)
            {
                return count;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return await CountAuditAsync(connection, resourceId);
    }

    private static async Task<long> CountAuditAsync(NpgsqlConnection connection, string resourceId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM audit.entries WHERE resource_id = @resourceId",
            connection);
        command.Parameters.AddWithValue("resourceId", resourceId);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<string[]?> QueryRowAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        var values = new string[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            values[index] = reader.GetValue(index).ToString() ?? string.Empty;
        }

        return values;
    }

    private static string NewEmail() => $"invitee-{Guid.NewGuid():N}@example.com";

    private static async Task<HttpResponseMessage> InviteAsync(HttpClient client, string email)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{SeededTenantId}/memberships")
        {
            Content = JsonContent.Create(new { email, roles = DefaultRoles }),
        };
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private static HttpClient CreateAdminClient(QepApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", AdminSubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", SeededTenantId);
        return client;
    }

    private sealed record MembershipPayload(Guid Id, Guid UserId, Guid TenantId, string State);

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, no heredado: appsettings.json lleva el proveedor con el que se despliega el
            // producto, y una suite de integración que depende de eso termina dependiendo de las
            // credenciales de quien la corra. Con "infobip" y las claves de Infobip ausentes —CI,
            // un clon nuevo— NotificationsOptionsValidator falla al arrancar y todas las pruebas
            // del archivo mueren antes de llegar a su aserción.
            // El canal de log es el default de desarrollo (SDD-CT-03). SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
