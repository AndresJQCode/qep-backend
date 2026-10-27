using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Notifications.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Identity.IntegrationTests;

/// <summary>
/// Helpers compartidos por las pruebas de registro y login con contraseña. Se importan con
/// <c>using static</c>. Cada ensamblado de pruebas tiene su propia copia: no hay proyecto compartido.
/// </summary>
internal static class PasswordAuthApiHarness
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

    // Mismas opciones que IdentityDbContextFactory: el historial de migraciones vive en el
    // esquema identity.
    public static IdentityDbContext NewIdentityContext(string connectionString) =>
        new(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "identity"))
            .Options);

    // Cada consumo de token va en su propio contexto, como en producción: TryConsumeAsync no
    // comparte la unidad de trabajo de nadie.
    public static EmailTokenRepository NewTokenRepository(string connectionString) =>
        new(NewIdentityContext(connectionString));

    public static async Task<DateTimeOffset?> EmailVerifiedAtAsync(string connectionString, Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT email_verified_at FROM identity.users WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", userId);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? null : new DateTimeOffset((DateTime)value, TimeSpan.Zero);
    }

    // `table` es siempre una constante de la prueba, nunca entrada externa.
    public static async Task<long> CountRowsAsync(string connectionString, string table, Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {table} WHERE user_id = @id", connection);
        command.Parameters.AddWithValue("id", userId);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // Sondea hasta que la condición se cumple. Copia de SessionRevocationTests.WaitUntilAsync con
    // el plazo parametrizable.
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < limit)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Condition was not met within {limit.TotalSeconds:0} seconds.");
    }

    public sealed record OutboxRow(string PayloadText, string CorrelationId, DateTimeOffset? ProcessedAt);

    // La fila más reciente del evento. `payload::text` devuelve el jsonb normalizado por Postgres.
    public static async Task<OutboxRow?> ReadOutboxRowAsync(string connectionString, string eventName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT payload::text, correlation_id, processed_at
            FROM platform.outbox_messages
            WHERE event_name = @event
            ORDER BY occurred_at DESC
            LIMIT 1
            """,
            connection);
        command.Parameters.AddWithValue("event", eventName);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        return new OutboxRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
    }
}

/// <summary>
/// Host real para las pruebas de registro y login con contraseña. Copia deliberada de
/// <c>RealAuthenticationApiTests.QepApiFactory</c>: entorno fuera de Development (rama de auth
/// real), y no quita ningún hosted service, así que corre el OutboxProcessor.
/// </summary>
internal sealed class PasswordAuthApiFactory(
    string connectionString,
    IEmailChannel? emailChannel = null,
    bool signupEnabled = true) : WebApplicationFactory<Program>
{
    public const string Audience = "test-audience";

    private static readonly string SigningKeyBase64 =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("IntegrationTests");
        builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
        builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
        builder.UseSetting("Storage:R2:AccountId", "test-account");
        builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
        builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
        builder.UseSetting("Storage:R2:Bucket", "test-bucket");
        builder.UseSetting("Notifications:EmailProvider", "log");
        builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
        builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
        builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
        builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        builder.UseSetting(
            "Registration:PublicTenantSignupEnabled", signupEnabled ? "true" : "false");
        builder.UseSetting("Authentication:Audience", Audience);
        builder.UseSetting("Authentication:TestSigningKey", SigningKeyBase64);
        builder.UseSetting("Platform:OperatorTenantId", string.Empty);
        if (emailChannel is not null)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailChannel>();
                services.AddSingleton(emailChannel);
            });
        }
    }
}
