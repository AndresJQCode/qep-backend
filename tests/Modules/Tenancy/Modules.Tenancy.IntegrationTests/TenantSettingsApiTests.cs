using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

public sealed class TenantSettingsApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    [Fact]
    public async Task GetAndPatchWithCurrentEtagUpdatesSettings()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var etag = await GetEtagAsync(client, TenantId);

        var patchResponse = await PatchAsync(client, TenantId, etag, NewDisplayName());

        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        Assert.NotNull(patchResponse.Headers.ETag);
        Assert.NotEqual(etag, patchResponse.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task TenantCannotReadOrUpdateAnotherTenantSettings()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        // Autenticado como OtherTenant, intentando alcanzar el tenant sembrado.
        using var client = CreateClient(factory, OtherSubjectId, OtherTenantId);

        var getResponse = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/settings",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, getResponse.StatusCode);

        var patchResponse = await PatchAsync(client, TenantId, "\"1\"", NewDisplayName());
        Assert.Equal(HttpStatusCode.Forbidden, patchResponse.StatusCode);
    }

    [Fact]
    public async Task MemberWithReadButWithoutUpdateCanReadButNotUpdate()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        using var client = CreateClient(factory, SubjectId, TenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", "tenancy.settings.read");

        var getResponse = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/settings",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var patchResponse = await PatchAsync(
            client,
            TenantId,
            getResponse.Headers.ETag!.Tag,
            NewDisplayName());
        Assert.Equal(HttpStatusCode.Forbidden, patchResponse.StatusCode);
    }

    [Fact]
    public async Task StaleEtagOnSecondUpdateYieldsPreconditionFailed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var staleEtag = await GetEtagAsync(client, TenantId);

        var firstPatch = await PatchAsync(client, TenantId, staleEtag, NewDisplayName());
        Assert.Equal(HttpStatusCode.OK, firstPatch.StatusCode);
        Assert.NotEqual(staleEtag, firstPatch.Headers.ETag?.Tag);

        // La segunda actualización reutiliza el ETag ya viejo, capturado antes de la primera.
        var secondPatch = await PatchAsync(client, TenantId, staleEtag, NewDisplayName());
        Assert.Equal(HttpStatusCode.PreconditionFailed, secondPatch.StatusCode);
    }

    [Fact]
    public async Task UpdateWritesAuditEntryAndCorrelatedOutboxEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var etag = await GetEtagAsync(client, TenantId);
        var patch = await PatchAsync(client, TenantId, etag, NewDisplayName());
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // La entrada de auditoría registra la acción, el actor y el campo cambiado.
        var audit = await QueryRowAsync(
            connection,
            """
            SELECT actor_id::text, outcome, changed_fields::text
            FROM audit.entries
            WHERE tenant_id = @tenantId AND action = 'tenancy.settings.updated'
            ORDER BY occurred_at DESC
            LIMIT 1
            """,
            TenantId);
        Assert.NotNull(audit);
        Assert.Equal(SubjectId, audit![0]);
        Assert.Equal("success", audit[1]);
        Assert.Contains("displayName", audit[2], StringComparison.Ordinal);

        // El evento de outbox lleva el nombre del evento de integración, un id de correlación y
        // el tenant en su payload — creado en la misma unidad de trabajo.
        var outbox = await QueryRowAsync(
            connection,
            """
            SELECT event_name, correlation_id, payload::text
            FROM platform.outbox_messages
            WHERE event_name = 'tenancy.tenant-settings-updated.v1'
            ORDER BY occurred_at DESC
            LIMIT 1
            """);
        Assert.NotNull(outbox);
        Assert.Equal("tenancy.tenant-settings-updated.v1", outbox![0]);
        Assert.False(string.IsNullOrWhiteSpace(outbox[1]));
        Assert.Contains(TenantId, outbox[2], StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string[]?> QueryRowAsync(
        NpgsqlConnection connection,
        string sql,
        string? tenantId = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (tenantId is not null)
        {
            command.Parameters.AddWithValue("tenantId", Guid.Parse(tenantId));
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

    private static string NewDisplayName() =>
        $"QCode {Guid.NewGuid():N}"[..24];

    private static async Task<string> GetEtagAsync(HttpClient client, string tenantId)
    {
        var response = await client.GetAsync(
            $"/api/v1/tenants/{tenantId}/settings",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        return response.Headers.ETag!.Tag;
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

    private static HttpClient CreateClient(
        QepApiFactory factory,
        string subjectId,
        string tenantId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        return client;
    }

    private static async Task<HttpResponseMessage> PatchAsync(
        HttpClient client,
        string tenantId,
        string ifMatch,
        string displayName)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/tenants/{tenantId}/settings")
        {
            Content = JsonContent.Create(new
            {
                displayName,
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "dd/MM/yyyy"
            })
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // UseSetting cae en builder.Configuration antes de que Program lea la cadena de
            // conexión al momento de registrar servicios. ConfigureAppConfiguration corre
            // demasiado tarde para esa lectura ansiosa y la app caería en silencio a
            // appsettings.json.
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
