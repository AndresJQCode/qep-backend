using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Modules.Identity.Application;
using Modules.Identity.Domain;
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

    // Cliente sobre https, como RealAuthenticationApiTests.CreateClient: la cookie de sesión es
    // Secure y con http el CookieContainer la guarda pero no la reenvía.
    //
    // TestServer no tiene RemoteIpAddress, así que toda prueba comparte la partición `unknown` del
    // limitador: máximo 10 requests por endpoint y por prueba, y cada prueba crea su propia fábrica
    // (nunca una fixture de clase).
    public static HttpClient CreateClient(PasswordAuthApiFactory factory) =>
        factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    // Id token con forma de Google firmado con la TestSigningKey de la fábrica. Copia de
    // RealAuthenticationApiTests.IssueGoogleIdToken.
    public static string IssueGoogleIdToken(string subject, string email, bool emailVerified)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(PasswordAuthApiFactory.SigningKeyBytes),
            SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: PasswordAuthApiFactory.Issuer,
            audience: PasswordAuthApiFactory.Audience,
            claims:
            [
                new Claim("sub", subject),
                new Claim("email", email),
                new Claim("email_verified", emailVerified ? "true" : "false"),
            ],
            notBefore: now.AddMinutes(-1),
            expires: now.AddMinutes(30),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-Qep-Client", "web");
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static Task<HttpResponseMessage> PostWithGoogleAsync(
        HttpClient client,
        string path,
        string idToken,
        object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body ?? new { }),
        };
        request.Headers.Add("X-Qep-Client", "web");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // Un usuario que se registró con contraseña y nunca verificó el email: el atacante que
    // pre-registra el correo de su víctima.
    public static async Task<Guid> SeedUnverifiedPasswordUserAsync(
        PasswordAuthApiFactory factory,
        string email,
        string password)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var now = DateTimeOffset.UtcNow;
        var user = User.CreateInvited(UserId.New(), email, now);
        user.Activate(now);
        services.GetRequiredService<IUserRepository>().Add(user);
        services.GetRequiredService<IPasswordCredentialRepository>().Add(
            PasswordCredential.Create(
                user.Id, services.GetRequiredService<IPasswordHasher>().Hash(password), now));
        await services.GetRequiredService<IEmailTokenIssuer>().IssueAsync(
            user.Id.Value, EmailTokenPurpose.Verify, TestContext.Current.CancellationToken);
        // El emisor no guarda: el token entra en la unidad de trabajo del llamador.
        await services.GetRequiredService<IIdentityUnitOfWork>().SaveChangesAsync(
            TestContext.Current.CancellationToken);
        return user.Id.Value;
    }

    public static async Task<long> CountUnusedTokensAsync(string connectionString, Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM identity.email_tokens WHERE user_id = @id AND used_at IS NULL",
            connection);
        command.Parameters.AddWithValue("id", userId);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    public static string NewSlug() => $"t-{Guid.NewGuid():N}"[..20];

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

    internal const string Issuer = "https://accounts.google.com";

    internal static readonly byte[] SigningKeyBytes = RandomNumberGenerator.GetBytes(32);

    private static readonly string SigningKeyBase64 = Convert.ToBase64String(SigningKeyBytes);

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

/// <summary>
/// El canal de correo de las pruebas: guarda lo enviado. Copia de
/// <c>NotificationsDeliveryHarness.RecordingEmailChannel</c>.
/// </summary>
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
