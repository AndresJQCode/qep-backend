using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// El límite por IP de los dos endpoints previos a la sesión: <c>POST /api/v1/auth/session</c> y
/// <c>POST /api/v1/auth/register-tenant</c>. Comparten la política <c>authentication</c> y por lo
/// tanto el mismo bucket por IP, aparte del de la política <c>public</c>.
/// </summary>
/// <remarks>
/// <para>
/// Los requests van sin credenciales a propósito: el rate limiter corre antes que la autenticación,
/// así que los permitidos devuelven 401 y el que se pasa del límite devuelve 429 sin llegar a validar
/// ningún token. Si el limitador quedara debajo de la autenticación, el 401 cortaría antes y estas
/// pruebas nunca verían el 429.
/// </para>
/// <para>
/// La IP del cliente se fija como en producción: el par directo es un nodo del ingress dentro de
/// <c>ForwardedHeaders:KnownNetworks</c> y la IP real viaja en <c>X-Real-IP</c> (ver
/// <see cref="ForwardedHeadersApiTests"/>).
/// </para>
/// </remarks>
public sealed class AuthenticationRateLimitApiTests
{
    // Mismo valor que la política authentication de Program.cs.
    private const int AuthenticationPermitLimit = 10;

    private const string SessionPath = "/api/v1/auth/session";
    private const string RegisterTenantPath = "/api/v1/auth/register-tenant";
    private const string OpenApiPath = "/openapi/v1.json";

    private const string TrustedNetwork = "10.50.0.0/24";

    private static readonly IPAddress IngressNode = IPAddress.Parse("10.50.0.21");

    private const string ClientA = "203.0.113.10";
    private const string ClientB = "203.0.113.20";

    [Fact]
    public async Task TheEleventhLoginFromTheSameIpIsRejectedAndAnotherIpIsNot()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new AuthenticationRateLimitApiFactory(database.GetConnectionString());

        await ExhaustAsync(factory, HttpMethods.Post, SessionPath, ClientA);

        var rejected = await SendAsync(factory, HttpMethods.Post, SessionPath, ClientA);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.StatusCode);
        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            (await SendAsync(factory, HttpMethods.Post, SessionPath, ClientB)).StatusCode);
    }

    [Fact]
    public async Task TheEleventhTenantRegistrationFromTheSameIpIsRejectedAndAnotherIpIsNot()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new AuthenticationRateLimitApiFactory(database.GetConnectionString());

        await ExhaustAsync(factory, HttpMethods.Post, RegisterTenantPath, ClientA);

        var rejected = await SendAsync(factory, HttpMethods.Post, RegisterTenantPath, ClientA);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.StatusCode);
        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            (await SendAsync(factory, HttpMethods.Post, RegisterTenantPath, ClientB)).StatusCode);
    }

    // El bucket es por IP y no por endpoint: quien martilla register-tenant también gasta su
    // presupuesto de login.
    [Fact]
    public async Task LoginAndTenantRegistrationShareTheSameBucket()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new AuthenticationRateLimitApiFactory(database.GetConnectionString());

        await ExhaustAsync(factory, HttpMethods.Post, RegisterTenantPath, ClientA);

        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            (await SendAsync(factory, HttpMethods.Post, SessionPath, ClientA)).StatusCode);
    }

    // Presupuestos distintos: agotar el de autenticación no le quita el documento OpenAPI a esa IP.
    [Fact]
    public async Task ExhaustingTheAuthenticationBudgetLeavesThePublicBudgetIntact()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new AuthenticationRateLimitApiFactory(database.GetConnectionString());

        await ExhaustAsync(factory, HttpMethods.Post, SessionPath, ClientA);
        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            (await SendAsync(factory, HttpMethods.Post, SessionPath, ClientA)).StatusCode);

        Assert.Equal(
            StatusCodes.Status200OK,
            (await SendAsync(factory, HttpMethods.Get, OpenApiPath, ClientA)).StatusCode);
    }

    // El 429 dice cuándo volver: los segundos que le quedan a la ventana fija de un minuto.
    [Fact]
    public async Task TheRejectionCarriesRetryAfter()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new AuthenticationRateLimitApiFactory(database.GetConnectionString());

        await ExhaustAsync(factory, HttpMethods.Post, SessionPath, ClientA);

        var rejected = await SendAsync(factory, HttpMethods.Post, SessionPath, ClientA);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.StatusCode);
        var retryAfter = rejected.Headers.RetryAfter.ToString();
        Assert.True(
            int.TryParse(retryAfter, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds),
            $"Retry-After should be whole seconds, got '{retryAfter}'.");
        Assert.InRange(seconds, 1, 60);
    }

    private static async Task ExhaustAsync(
        AuthenticationRateLimitApiFactory factory, string method, string path, string realIp)
    {
        for (var i = 0; i < AuthenticationPermitLimit; i++)
        {
            Assert.Equal(
                StatusCodes.Status401Unauthorized,
                (await SendAsync(factory, method, path, realIp)).StatusCode);
        }
    }

    private static async Task<HttpResponse> SendAsync(
        AuthenticationRateLimitApiFactory factory, string method, string path, string realIp)
    {
        var context = await factory.Server.SendAsync(
            http =>
            {
                http.Request.Method = method;
                http.Request.Path = path;
                http.Request.Headers["X-Real-IP"] = realIp;
                http.Request.Headers["X-Forwarded-Proto"] = "https";
                http.Connection.RemoteIpAddress = IngressNode;
            },
            TestContext.Current.CancellationToken);
        return context.Response;
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

    private sealed class AuthenticationRateLimitApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            // Fijado, nunca heredado: con el stub y sin X-Subject-Id/X-Tenant-Id, los requests
            // permitidos dan 401 sin depender de Google ni del middleware de CSRF.
            builder.UseSetting("Authentication:UseDevelopmentStub", "true");
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "false");
            builder.UseSetting("ForwardedHeaders:KnownNetworks:0", TrustedNetwork);
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
        }
    }
}
