using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// CORS para la SPA en <c>https://qep.qcode.co</c>, que llama a la API en
/// <c>https://qep-api.qcode.co</c> directo y no por el rewrite de Vercel. Los dos hosts cuelgan de
/// <c>qcode.co</c>: son el mismo sitio —la cookie <c>SameSite=Lax</c> viaja— pero orígenes
/// distintos, así que el navegador exige CORS con credenciales.
/// </summary>
/// <remarks>
/// La defensa CSRF (<c>RequireCsrfHeaderMiddleware</c>) se apoya en que el navegador no manda
/// <c>X-Qep-Client</c> desde otro origen sin un preflight exitoso. Por eso estas pruebas miden
/// tanto lo que se permite como lo que no: otros orígenes —incluidas otras apps bajo
/// <c>*.qcode.co</c> y el dominio viejo de Vercel— no reciben <c>Access-Control-Allow-Origin</c>,
/// y un header fuera de la lista tumba el preflight entero.
/// </remarks>
public sealed class CorsApiTests
{
    private const string SpaOrigin = "https://qep.qcode.co";
    private const string RequestedHeaders = "x-qep-client, x-tenant-id, content-type, authorization, if-match";

    private static readonly string[] DisallowedOrigins =
    [
        "https://evil.example",
        "https://other-app.qcode.co",
        "https://qep-frontend.vercel.app",
    ];

    private static readonly string[] AdvisorRoles = ["advisor"];

    private static readonly string SigningKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public async Task PreflightFromTheSpaOriginAllowsTheRequestWithCredentials()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new CorsApiFactory(database.GetConnectionString(), SpaOrigin);
        using var client = factory.CreateClient();

        using var response = await SendPreflightAsync(client, SpaOrigin, "/api/v1/auth/session", "POST", RequestedHeaders);

        Assert.True(response.IsSuccessStatusCode, $"Preflight answered {(int)response.StatusCode}.");
        Assert.Equal(SpaOrigin, Single(response, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Single(response, "Access-Control-Allow-Credentials"));
        Assert.Contains("POST", Values(response, "Access-Control-Allow-Methods"), StringComparer.OrdinalIgnoreCase);
        var allowedHeaders = Values(response, "Access-Control-Allow-Headers");
        foreach (var header in RequestedHeaders.Split(", "))
        {
            Assert.Contains(header, allowedHeaders, StringComparer.OrdinalIgnoreCase);
        }

        Assert.Equal("600", Single(response, "Access-Control-Max-Age"));
    }

    // Cada método que la API mapea tiene que pasar el preflight: PUT lleva If-Match (configuración
    // del tenant), PATCH la metadata de un archivo y DELETE los borrados del catálogo.
    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task PreflightFromTheSpaOriginAllowsEveryMappedMethod(string method)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new CorsApiFactory(database.GetConnectionString(), SpaOrigin);
        using var client = factory.CreateClient();

        using var response = await SendPreflightAsync(
            client, SpaOrigin, $"/api/v1/tenants/{Guid.NewGuid()}/settings", method, RequestedHeaders);

        Assert.Equal(SpaOrigin, Single(response, "Access-Control-Allow-Origin"));
        Assert.Contains(method, Values(response, "Access-Control-Allow-Methods"), StringComparer.OrdinalIgnoreCase);
    }

    // Otras apps bajo *.qcode.co son el mismo sitio: si CORS las dejara pasar, podrían mandar
    // X-Qep-Client con la cookie de la sesión y la defensa CSRF no serviría de nada.
    [Fact]
    public async Task PreflightFromAnyOtherOriginGetsNoCorsHeaders()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new CorsApiFactory(database.GetConnectionString(), SpaOrigin);
        using var client = factory.CreateClient();

        foreach (var origin in DisallowedOrigins)
        {
            using var response = await SendPreflightAsync(client, origin, "/api/v1/auth/session", "POST", RequestedHeaders);

            Assert.False(
                response.Headers.Contains("Access-Control-Allow-Origin"),
                $"{origin} got Access-Control-Allow-Origin.");
            Assert.False(
                response.Headers.Contains("Access-Control-Allow-Credentials"),
                $"{origin} got Access-Control-Allow-Credentials.");
        }
    }

    // La lista de headers es exacta, no AllowAnyHeader: un header que la SPA no manda tumba el
    // preflight, aunque el origen sea el correcto. ASP.NET Core igual contesta el origen; lo que
    // hace fallar al navegador es que Access-Control-Allow-Headers no lo nombre. X-Permissions es
    // el del stub de desarrollo, el ejemplo de un header que nunca debe pasar.
    [Fact]
    public async Task PreflightRequestingAnUnlistedHeaderIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new CorsApiFactory(database.GetConnectionString(), SpaOrigin);
        using var client = factory.CreateClient();

        using var response = await SendPreflightAsync(
            client, SpaOrigin, "/api/v1/auth/session", "POST", "x-qep-client, x-permissions");

        Assert.DoesNotContain(
            "x-permissions", Values(response, "Access-Control-Allow-Headers"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActualRequestFromTheSpaOriginCarriesCorsHeadersAndOthersDoNot()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new CorsApiFactory(database.GetConnectionString(), SpaOrigin);
        using var client = factory.CreateClient();

        using var allowed = await GetAsync(client, SpaOrigin, "/api/v1/auth/registration-policy");

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(SpaOrigin, Single(allowed, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Single(allowed, "Access-Control-Allow-Credentials"));
        // La SPA sólo lee Content-Type y Content-Length, que ya son safelisted.
        Assert.False(allowed.Headers.Contains("Access-Control-Expose-Headers"));

        foreach (var origin in DisallowedOrigins)
        {
            using var denied = await GetAsync(client, origin, "/api/v1/auth/registration-policy");

            Assert.False(
                denied.Headers.Contains("Access-Control-Allow-Origin"),
                $"{origin} got Access-Control-Allow-Origin.");
        }
    }

    // Sin Access-Control-Allow-Origin en el 401, el navegador le esconde la respuesta a la SPA, que
    // no puede distinguir "sin sesión" de "la API no responde".
    [Fact]
    public async Task UnauthorizedResponseToTheSpaOriginCarriesCorsHeaders()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new CorsApiFactory(database.GetConnectionString(), SpaOrigin);
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, SpaOrigin, "/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(SpaOrigin, Single(response, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Single(response, "Access-Control-Allow-Credentials"));
    }

    // El 422 lo escribe ApiExceptionHandler, que limpia la respuesta antes de escribir el
    // ProblemDetails. Los headers de CORS tienen que sobrevivir a eso: el formulario lee el mapa
    // errors para marcar el input. Con el stub, que es la forma barata de llegar al validador.
    [Fact]
    public async Task ErrorWrittenByTheExceptionHandlerCarriesCorsHeaders()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new CorsApiFactory(database.GetConnectionString(), SpaOrigin)
        {
            UseDevelopmentStub = true,
        };
        using var client = factory.CreateClient();
        var tenantId = Guid.NewGuid();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email = $"cors-{Guid.NewGuid():N}@example.com", roles = AdvisorRoles }),
        };
        request.Headers.Add("Origin", SpaOrigin);
        request.Headers.Add("X-Subject-Id", Guid.NewGuid().ToString());
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(SpaOrigin, Single(response, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Single(response, "Access-Control-Allow-Credentials"));
    }

    // CORS no reemplaza a la defensa CSRF: desde el origen permitido, un request que muta sin
    // X-Qep-Client sigue saliendo 403. Y el 403 lleva CORS, para que la SPA lo pueda leer.
    [Fact]
    public async Task MutatingRequestFromTheSpaOriginWithoutTheClientHeaderIsStillRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new CorsApiFactory(database.GetConnectionString(), SpaOrigin);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add("Origin", SpaOrigin);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.Equal("Missing required client header.", problem?.Title);
        Assert.Equal(SpaOrigin, Single(response, "Access-Control-Allow-Origin"));
    }

    // Sin la clave no hay CORS: es como corre en local (proxy de Vite) y en las demás pruebas.
    [Fact]
    public async Task WithoutConfiguredOriginsThereIsNoCors()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new CorsApiFactory(database.GetConnectionString());
        using var client = factory.CreateClient();

        using var preflight = await SendPreflightAsync(client, SpaOrigin, "/api/v1/auth/session", "POST", RequestedHeaders);
        using var actual = await GetAsync(client, SpaOrigin, "/api/v1/auth/registration-policy");

        // El 405 de siempre, no el 204 que daría el middleware de CORS registrado con la lista
        // vacía: sin orígenes no se registra nada.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, preflight.StatusCode);
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.False(actual.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // Un comodín de subdominio abriría la API a todas las apps bajo qcode.co: tumba el arranque.
    [Fact]
    public async Task AWildcardOriginStopsTheStartup()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new CorsApiFactory(database.GetConnectionString(), "https://*.qcode.co");

        var exception = Assert.ThrowsAny<Exception>(() => factory.Server);

        Assert.Contains("Cors:AllowedOrigins:0", exception.ToString(), StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendPreflightAsync(
        HttpClient client, string origin, string path, string method, string headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", headers);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string origin, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", origin);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Single(HttpResponseMessage response, string header) =>
        Assert.Single(response.Headers.TryGetValues(header, out var values) ? values : []);

    // Access-Control-Allow-Methods y -Headers pueden venir como lista separada por comas.
    private static string[] Values(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values)
            ? values
                .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToArray()
            : [];

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

    private sealed class CorsApiFactory(string connectionString, params string[] allowedOrigins)
        : WebApplicationFactory<Program>
    {
        private readonly string[] _allowedOrigins = allowedOrigins;

        /// <summary>
        /// Por defecto, autenticación real: es la única rama que registra la defensa CSRF, y la que
        /// corre en producción. El stub sólo para llegar barato a un error del manejador.
        /// </summary>
        public bool UseDevelopmentStub { get; init; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Ni "Development" ni "Local" para la rama real: mismo criterio que
            // RealAuthenticationApiTests (SDD-CT-14).
            builder.UseEnvironment(UseDevelopmentStub ? "Development" : "IntegrationTests");
            builder.UseSetting("Authentication:UseDevelopmentStub", UseDevelopmentStub ? "true" : "false");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, nunca heredado: mismo criterio que TenantSettingsApiTests (SDD-CT-17).
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
            builder.UseSetting("Authentication:Audience", "test-audience");
            builder.UseSetting("Authentication:TestSigningKey", SigningKeyBase64);
            for (var i = 0; i < _allowedOrigins.Length; i++)
            {
                builder.UseSetting($"Cors:AllowedOrigins:{i}", _allowedOrigins[i]);
            }
        }
    }
}
