using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Notifications.IntegrationTests;

public sealed class InvitationNotificationTests
{
    private const string SeededTenantId = "01900000-0000-7000-8000-000000000001";
    private const string AdminSubjectId = "01900000-0000-7000-8000-000000000002";
    private static readonly string[] DefaultRoles = ["tenancy.member"];

    [Fact]
    public async Task InvitingMemberDeliversExactlyOneInvitationNotification()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateAdminClient(factory);
        var email = NewEmail();

        var invite = await InviteAsync(client, email);
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // El worker de fondo consume el evento de Outbox de membresía invitada y
        // entrega el email de invitación por el canal de log de desarrollo.
        var status = await WaitForNotificationStatusAsync(connection, email);
        Assert.Equal("Sent", status);

        // Idempotente: el inbox del worker impide una segunda notificación para el mismo
        // mensaje de Outbox, incluso a través de ticks de sondeo posteriores.
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        var count = await CountNotificationsAsync(connection, email);
        Assert.Equal(1L, count);
    }

    private static async Task<string?> WaitForNotificationStatusAsync(
        NpgsqlConnection connection,
        string email)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                "SELECT status FROM notifications.notifications WHERE recipient_address = @email",
                connection);
            command.Parameters.AddWithValue("email", email);
            var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            if (result is string status)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return null;
    }

    private static async Task<long> CountNotificationsAsync(
        NpgsqlConnection connection,
        string email)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM notifications.notifications WHERE recipient_address = @email",
            connection);
        command.Parameters.AddWithValue("email", email);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
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
