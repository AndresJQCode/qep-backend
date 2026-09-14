using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// Los dos correos de la exportación asíncrona (spec 2026-09-12, D12). El evento se escribe
/// directo en el outbox, como lo deja Quotations: lo que se prueba acá es que Notifications lo
/// consume y le escribe a quien pidió la exportación, no cómo se arma el Excel.
/// </summary>
public sealed class QuotationsExportNotificationTests
{
    [Fact]
    public async Task TheReadyEventDeliversOneReadyEmailToWhoAskedForTheExport()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, email) = await RegisterOwnerAsync(factory);

        await InsertOutboxAsync(
            database.GetConnectionString(),
            "quotations.export-ready.v1",
            JsonSerializer.Serialize(new
            {
                tenantId,
                subjectId = ownerUserId,
                kind = "Quotations",
                downloadUrl = "https://r2.test/exports/x.xlsx?X-Amz-Signature=abc",
                fileName = "cotizaciones-2026-09-12-1530.xlsx",
                rowCount = 3,
                expiresAt = DateTimeOffset.UtcNow.AddHours(24),
            }));

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var delivered = await WaitForNotificationAsync(connection, ownerUserId, "quotations.export-ready.v1");
        Assert.Equal(("Sent", email), delivered);

        // Idempotente por el inbox: los ticks siguientes no vuelven a mandar el mismo evento.
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Equal(1L, await CountNotificationsAsync(connection, ownerUserId, "quotations.export-ready.v1"));
    }

    [Fact]
    public async Task TheFailedEventDeliversTheFailedEmail()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, email) = await RegisterOwnerAsync(factory);

        await InsertOutboxAsync(
            database.GetConnectionString(),
            "quotations.export-failed.v1",
            JsonSerializer.Serialize(new { tenantId, subjectId = ownerUserId, kind = "Orders" }));

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var delivered = await WaitForNotificationAsync(connection, ownerUserId, "quotations.export-failed.v1");
        Assert.Equal(("Sent", email), delivered);
    }

    // register-tenant es la única forma de tener un usuario con correo en identity.users sin la
    // vuelta de Google: el stub toma el correo del header X-Email. Mismo mecanismo que
    // QuotationsApiHarness.RegisterTenantAsync.
    private static async Task<(Guid TenantId, Guid OwnerUserId, string Email)> RegisterOwnerAsync(
        QepApiFactory factory)
    {
        var email = $"owner-{Guid.NewGuid():N}@example.com";
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add("X-Email", email);
        client.DefaultRequestHeaders.Add("X-Email-Verified", "true");

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Notifications Export Org",
                slug = $"org-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<RegisteredTenantDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(registered);
        return (registered.TenantId, registered.OwnerUserId, email);
    }

    private static async Task InsertOutboxAsync(string connectionString, string eventName, string payload)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, attempts)
            VALUES (@id, @eventName, CAST(@payload AS jsonb), @correlationId, @occurredAt, 0)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("eventName", eventName);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("correlationId", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("occurredAt", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<(string Status, string Recipient)?> WaitForNotificationAsync(
        NpgsqlConnection connection, Guid recipientId, string templateRef)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT status, recipient_address FROM notifications.notifications
                WHERE recipient_id = @recipientId AND template_ref = @templateRef
                """,
                connection);
            command.Parameters.AddWithValue("recipientId", recipientId);
            command.Parameters.AddWithValue("templateRef", templateRef);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            if (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                return (reader.GetString(0), reader.GetString(1));
            }

            await reader.CloseAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return null;
    }

    private static async Task<long> CountNotificationsAsync(
        NpgsqlConnection connection, Guid recipientId, string templateRef)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM notifications.notifications
            WHERE recipient_id = @recipientId AND template_ref = @templateRef
            """,
            connection);
        command.Parameters.AddWithValue("recipientId", recipientId);
        command.Parameters.AddWithValue("templateRef", templateRef);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
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

    private sealed record RegisteredTenantDto(Guid TenantId, Guid OwnerUserId);

    // Misma factoría que InvitationNotificationTests: cada archivo de este proyecto arma la suya.
    private sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
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
            // Fijado, nunca heredado: deja explícito que el correo sale por el canal de log.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
