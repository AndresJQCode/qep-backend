using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
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

    public static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client,
        string email,
        string password,
        string slug) =>
        PostJsonAsync(
            client,
            "/api/v1/auth/password/register-tenant",
            new { email, password, displayName = "Acme", slug });

    public sealed record ProblemPayload(
        string? Code,
        string? Title,
        string? Detail,
        Dictionary<string, string[]>? Errors);

    // `code` y `errors` son extensiones de primer nivel del ProblemDetails.
    public static async Task<ProblemPayload> ProblemAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        string? Text(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        Dictionary<string, string[]>? errors = null;
        if (root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Object)
        {
            errors = errorsElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.EnumerateArray().Select(item => item.GetString()!).ToArray());
        }

        return new ProblemPayload(Text("code"), Text("title"), Text("detail"), errors);
    }

    // `payload::text` de cada fila del evento para ese usuario, en orden de occurred_at.
    public static async Task<IReadOnlyList<string>> ReadOutboxAsync(
        string connectionString,
        string eventName,
        Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT payload::text
            FROM platform.outbox_messages
            WHERE event_name = @event AND payload->>'userId' = @userId
            ORDER BY occurred_at
            """,
            connection);
        command.Parameters.AddWithValue("event", eventName);
        command.Parameters.AddWithValue("userId", userId.ToString());
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    // Lo que la prueba lee del cuerpo de /auth/session y /auth/me (SessionResponse).
    public sealed record SessionTenantPayload(Guid TenantId, string DisplayName);

    public sealed record SessionPayload(
        Guid UserId,
        string? Email,
        IReadOnlyList<SessionTenantPayload> ActiveTenants);

    public static Task<HttpResponseMessage> VerifyAsync(HttpClient client, string token) =>
        PostJsonAsync(client, "/api/v1/auth/email/verify", new { token });

    // El único lugar donde una prueba puede leer un token crudo: la base guarda sólo el hash.
    public static async Task<string> LatestOutboxTokenAsync(
        string connectionString,
        string eventName,
        Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT payload->>'token'
            FROM platform.outbox_messages
            WHERE event_name = @event AND payload->>'userId' = @userId
            ORDER BY occurred_at DESC
            LIMIT 1
            """,
            connection);
        command.Parameters.AddWithValue("event", eventName);
        command.Parameters.AddWithValue("userId", userId.ToString());
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value as string
            ?? throw new InvalidOperationException($"No {eventName} outbox row for user {userId}.");
    }

    // Todo el texto de la auditoría como JSON, para buscar secretos con Assert.DoesNotContain.
    public static Task<string> AllAuditTextAsync(string connectionString) =>
        AllRowsAsTextAsync(connectionString, "audit.entries");

    // Todo el texto de las notificaciones (destinatario, asunto, cuerpos) como JSON.
    public static Task<string> AllNotificationTextAsync(string connectionString) =>
        AllRowsAsTextAsync(connectionString, "notifications.notifications");

    private static async Task<string> AllRowsAsTextAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT coalesce(string_agg(row_to_json(e)::text, ''), '') FROM {table} e",
            connection);
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public sealed record RegisteredOwner(Guid UserId, Guid TenantId);

    // Registra, toma el token de la outbox y lo verifica: deja al cliente con la cookie de sesión.
    public static async Task<RegisteredOwner> RegisterAndVerifyAsync(
        HttpClient client,
        string connectionString,
        string email,
        string password)
    {
        var registered = await RegisterAsync(client, email, password, NewSlug());
        Assert.Equal(System.Net.HttpStatusCode.Accepted, registered.StatusCode);
        var userId = await FindUserIdAsync(connectionString, email);
        var token = await LatestOutboxTokenAsync(
            connectionString, IdentityIntegrationEvents.EmailVerificationRequested, userId);
        var verified = await VerifyAsync(client, token);
        Assert.Equal(System.Net.HttpStatusCode.OK, verified.StatusCode);
        var session = await verified.Content.ReadFromJsonAsync<SessionPayload>(
            TestContext.Current.CancellationToken);
        return new RegisteredOwner(userId, Assert.Single(session!.ActiveTenants).TenantId);
    }

    public static async Task<Guid> FindUserIdAsync(string connectionString, string email)
    {
        var value = await ScalarAsync(
            connectionString,
            "SELECT id FROM identity.users WHERE email = @value",
            User.NormalizeEmail(email));
        return value is Guid id ? id : throw new InvalidOperationException($"No user with email {email}.");
    }

    public static async Task<long> CountTenantsWithSlugAsync(string connectionString, string slug) =>
        (long)(await ScalarAsync(connectionString, "SELECT COUNT(*) FROM tenancy.tenants WHERE slug = @value", slug))!;

    public static async Task<long> CountAuditAsync(string connectionString, Guid resourceId, string action)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM audit.entries WHERE resource_id = @id AND action = @action", connection);
        command.Parameters.AddWithValue("id", resourceId.ToString());
        command.Parameters.AddWithValue("action", action);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public static Task<HttpResponseMessage> ForgotAsync(HttpClient client, string email) =>
        PostJsonAsync(client, "/api/v1/auth/password/forgot", new { email });

    public static Task<HttpResponseMessage> ResetAsync(HttpClient client, string token, string password) =>
        PostJsonAsync(client, "/api/v1/auth/password/reset", new { token, password });

    public static async Task<string?> MembershipStateAsync(string connectionString, Guid tenantId, Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT state FROM tenancy.memberships WHERE tenant_id = @tenantId AND user_id = @userId",
            connection);
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("userId", userId);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value as string;
    }

    public static async Task<string?> UserStatusAsync(string connectionString, Guid userId) =>
        await ScalarAsync(
            connectionString,
            "SELECT status FROM identity.users WHERE id = @value",
            userId) as string;

    private static readonly string[] AdvisorRoles = ["advisor"];

    public sealed record InvitedMember(Guid MembershipId, Guid UserId, string Token);

    // Invita por la API como el dueño (la cookie del cliente) y toma el token crudo del payload de
    // tenancy.membership-invited.v1: la base guarda sólo el hash.
    public static async Task<InvitedMember> InviteAsync(
        HttpClient ownerClient,
        string connectionString,
        Guid tenantId,
        string email)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new
            {
                email,
                displayName = "Ana Pérez",
                roles = AdvisorRoles,
                advisorCode = (int?)null,
            }),
        };
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        request.Headers.Add("X-Qep-Client", "web");
        var response = await ownerClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var membershipId = body.RootElement.GetProperty("id").GetGuid();
        var userId = body.RootElement.GetProperty("userId").GetGuid();

        var token = await ScalarAsync(
            connectionString,
            """
            SELECT payload->>'token' FROM platform.outbox_messages
            WHERE event_name = 'tenancy.membership-invited.v1'
              AND payload::text LIKE '%' || @value || '%'
            ORDER BY occurred_at DESC
            LIMIT 1
            """,
            membershipId.ToString()) as string;
        return new InvitedMember(
            membershipId,
            userId,
            token ?? throw new InvalidOperationException($"No invitation token for membership {membershipId}."));
    }

    public sealed record InvitationPayload(
        Guid TenantId,
        string TenantName,
        string Email,
        string Status,
        bool HasPassword);

    public static async Task<InvitationPayload> GetInvitationAsync(HttpClient client, string token)
    {
        var response = await client.GetAsync(
            $"/api/v1/invitations/{token}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InvitationPayload>(
            TestContext.Current.CancellationToken))!;
    }

    public static Task<HttpResponseMessage> AcceptWithPasswordAsync(
        HttpClient client,
        string token,
        string password) =>
        PostJsonAsync(client, $"/api/v1/invitations/{token}/accept-with-password", new { password });

    public static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        PostJsonAsync(client, "/api/v1/auth/password/login", new { email, password });

    // Lo comparable de un error entre dos respuestas: sin traceId ni nada que cambie por request.
    public static async Task<(HttpStatusCode Status, string? Code, string? Title, string? Detail)> ShapeAsync(
        HttpResponseMessage response)
    {
        var problem = await ProblemAsync(response);
        return (response.StatusCode, problem.Code, problem.Title, problem.Detail);
    }

    public static async Task<long> CountAuditByResourceAsync(
        string connectionString,
        string action,
        string resourceId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM audit.entries WHERE action = @action AND resource_id = @resourceId",
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("resourceId", resourceId);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // Filas de audit.entries que contienen `text` en cualquier columna.
    public static async Task<long> CountAuditContainingAsync(string connectionString, string text)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM audit.entries e WHERE row_to_json(e)::text LIKE '%' || @text || '%'",
            connection);
        command.Parameters.AddWithValue("text", text);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public static async Task<string?> CredentialHashAsync(string connectionString, Guid userId) =>
        await ScalarAsync(
            connectionString,
            "SELECT hash FROM identity.password_credentials WHERE user_id = @value",
            userId) as string;

    public static async Task<long> CountUsersWithEmailAsync(string connectionString, string email) =>
        (long)(await ScalarAsync(
            connectionString,
            "SELECT COUNT(*) FROM identity.users WHERE email = @value",
            User.NormalizeEmail(email)))!;

    public static async Task<long> CountOutboxAsync(string connectionString, string eventName) =>
        (long)(await ScalarAsync(
            connectionString,
            "SELECT COUNT(*) FROM platform.outbox_messages WHERE event_name = @value",
            eventName))!;

    public static async Task<long> CountTokensWithHashAsync(string connectionString, string tokenHash) =>
        (long)(await ScalarAsync(
            connectionString,
            "SELECT COUNT(*) FROM identity.email_tokens WHERE token_hash = @value",
            tokenHash))!;

    // Una sola consulta con un parámetro `@value`; el SQL es siempre una constante de la prueba.
    private static async Task<object?> ScalarAsync(string connectionString, string sql, object value)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is DBNull ? null : result;
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
