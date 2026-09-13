using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Modules.Notifications.Application;
using Modules.Notifications.Infrastructure.Messaging;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas del reclamo y de los workers de correo (spec 2026-09-13,
/// Sección 1). Los dos archivos que ya existían —InvitationNotificationTests y
/// QuotationsExportNotificationTests— conservan su factoría propia a propósito: el spec pide que
/// sigan verdes sin cambios.
///
/// Los helpers de SQL crudo (FindInboxAsync, PutInboxAsync, InsertOutboxAsync,
/// NotificationsForAsync) sólo funcionan DESPUÉS de que el host arrancó: es el host el que corre
/// las migraciones. Si no hay un ClaimAsync o un request HTTP previo que lo dispare, hay que
/// forzarlo con <c>_ = factory.Services;</c> antes de llamarlos, como hace
/// AProcessedMessageIsNeverClaimedAgain.
/// </summary>
internal static class NotificationsDeliveryHarness
{
    public static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    public static async Task<InboxRow?> FindInboxAsync(string connectionString, string consumer, Guid messageId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT processed_at, claimed_until, attempts FROM notifications.inbox_messages
            WHERE consumer = @consumer AND message_id = @messageId
            """,
            connection);
        command.Parameters.AddWithValue("consumer", consumer);
        command.Parameters.AddWithValue("messageId", messageId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        return new InboxRow(
            reader.IsDBNull(0) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(0),
            reader.IsDBNull(1) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetInt32(2));
    }

    /// <summary>Deja la fila del inbox como la habría dejado un worker anterior: por ejemplo, uno que
    /// reclamó y murió antes de guardar.</summary>
    public static async Task PutInboxAsync(
        string connectionString,
        string consumer,
        Guid messageId,
        DateTimeOffset? processedAt,
        DateTimeOffset? claimedUntil,
        int attempts)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO notifications.inbox_messages (consumer, message_id, processed_at, claimed_until, attempts)
            VALUES (@consumer, @messageId, @processedAt, @claimedUntil, @attempts)
            ON CONFLICT (consumer, message_id) DO UPDATE
                SET processed_at = EXCLUDED.processed_at,
                    claimed_until = EXCLUDED.claimed_until,
                    attempts = EXCLUDED.attempts
            """,
            connection);
        command.Parameters.AddWithValue("consumer", consumer);
        command.Parameters.AddWithValue("messageId", messageId);
        command.Parameters.Add(new NpgsqlParameter("processedAt", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)processedAt ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("claimedUntil", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)claimedUntil ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("attempts", attempts);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    // Los cinco derivan de OutboxDeliveryWorker (spec 2026-09-13, A6): un worker de correo nuevo queda
    // afuera de las pruebas sin que nadie tenga que acordarse de agregarlo acá.
    public static bool IsDeliveryWorker(Type? type) =>
        type is not null && typeof(OutboxDeliveryWorker).IsAssignableFrom(type);

    // register-tenant es la única forma de tener un usuario con correo en identity.users sin la vuelta
    // de Google: el stub toma el correo del header X-Email. Mismo mecanismo que
    // QuotationsExportNotificationTests.RegisterOwnerAsync.
    public static async Task<(Guid TenantId, Guid OwnerUserId, string Email)> RegisterOwnerAsync(
        NotificationsApiFactory factory)
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
                displayName = "Notifications Delivery Org",
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

    /// <summary>El evento se escribe directo en el outbox, como lo deja el módulo productor.</summary>
    public static async Task<Guid> InsertOutboxAsync(
        string connectionString, string eventName, string payload, DateTimeOffset? occurredAt = null)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, attempts)
            VALUES (@id, @eventName, CAST(@payload AS jsonb), @correlationId, @occurredAt, 0)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("eventName", eventName);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("correlationId", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("occurredAt", occurredAt ?? DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    public static async Task<IReadOnlyList<NotificationRow>> NotificationsForAsync(
        string connectionString, Guid recipientId, string templateRef)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT status, recipient_address, failure_reason FROM notifications.notifications
            WHERE recipient_id = @recipientId AND template_ref = @templateRef
            ORDER BY created_at
            """,
            connection);
        command.Parameters.AddWithValue("recipientId", recipientId);
        command.Parameters.AddWithValue("templateRef", templateRef);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<NotificationRow>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new NotificationRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return rows;
    }

    // Sondeo con plazo, igual que InvitationNotificationTests: el tick es del worker, no de la prueba.
    public static async Task<NotificationRow?> WaitForNotificationAsync(
        string connectionString, Guid recipientId, string templateRef)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var rows = await NotificationsForAsync(connectionString, recipientId, templateRef);
            if (rows.Count > 0)
            {
                return rows[0];
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return null;
    }

    private sealed record RegisteredTenantDto(Guid TenantId, Guid OwnerUserId);
}

internal sealed record InboxRow(DateTimeOffset? ProcessedAt, DateTimeOffset? ClaimedUntil, int Attempts);

internal sealed record NotificationRow(string Status, string RecipientAddress, string? FailureReason);

/// <summary>El canal de correo de las pruebas: cuenta los envíos y puede demorarse o fallar a pedido.</summary>
internal sealed class RecordingEmailChannel : IEmailChannel
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();
    private int _calls;

    /// <summary>Lo que pasa en cada envío antes de contarlo: esperar para abrir una carrera, o
    /// fallar. Recibe el número de llamada, empezando en 1.</summary>
    public Func<int, Task>? OnSend { get; init; }

    /// <summary>Sólo los envíos que terminaron bien.</summary>
    public IReadOnlyCollection<EmailMessage> Sent => _sent;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        if (OnSend is { } onSend)
        {
            await onSend(call);
        }

        _sent.Enqueue(message);
    }
}

internal sealed class NotificationsApiFactory(
    string connectionString,
    IEmailChannel? emailChannel = null,
    bool runDeliveryWorkers = false)
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
        // Fijado, nunca heredado: con "infobip" y sus claves ausentes, el validador de Notifications
        // falla al arrancar y todas las pruebas del archivo mueren antes de su aserción.
        builder.UseSetting("Notifications:EmailProvider", "log");
        builder.ConfigureServices(services =>
        {
            if (emailChannel is not null)
            {
                services.RemoveAll<IEmailChannel>();
                services.AddSingleton<IEmailChannel>(emailChannel);
            }

            // Los cinco workers de correo sondean cada 3 s por su cuenta. En una prueba que llama a
            // DrainAsync competirían con ella y la volverían no determinista. Mismo interruptor que
            // runExportWorker en QuotationsApiHarness: sólo lo prende la prueba que los necesita.
            if (!runDeliveryWorkers)
            {
                var workers = services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                        && NotificationsDeliveryHarness.IsDeliveryWorker(descriptor.ImplementationType))
                    .ToList();
                foreach (var descriptor in workers)
                {
                    services.Remove(descriptor);
                }
            }
        });
    }
}
