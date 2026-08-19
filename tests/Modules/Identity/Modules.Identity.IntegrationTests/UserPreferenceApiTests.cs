using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Modules.Identity.IntegrationTests;

/// <summary>
/// ACC-03. Cubre <c>GET</c>/<c>PUT /api/v1/auth/preferences</c>.
///
/// Lo que más importa acá es <c>CA-ACC-03-04</c>: la preferencia es del usuario **en cada
/// tenant** (SDD-OD-17), así que dos tenants del mismo usuario tienen que quedar aislados.
///
/// El `403` del endpoint **no se prueba acá**: por el camino del stub, un request sin tenant
/// muere en la autenticación con `401` y nunca llega. Ver el comentario de
/// <see cref="WithoutTenantTheStubRejectsBeforeReachingTheEndpoint"/>.
/// </summary>
public sealed class UserPreferenceApiTests
{
    private const string Endpoint = "/api/v1/auth/preferences";
    private const string SubjectId = "01900000-0000-7000-8000-0000000000a1";
    private const string TenantId = "01900000-0000-7000-8000-0000000000b1";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000b2";

    private sealed record PreferenceBody(string ColorScheme, string Mode);

    [Fact]
    public async Task GetReturnsTheDefaultForSomeoneWhoNeverChose()
    {
        // CA-ACC-03-01. No tener preferencia es un estado normal, no un 404: así el default
        // no queda duplicado en el cliente.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedUserAsync(factory, SubjectId);
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await client.GetAsync(Endpoint, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PreferenceBody>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal("botanical", body!.ColorScheme);
        Assert.Equal("light", body.Mode);
    }

    [Fact]
    public async Task PutPersistsAndGetReadsItBack()
    {
        // CA-ACC-03-02.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedUserAsync(factory, SubjectId);
        using var client = CreateClient(factory, SubjectId, TenantId);

        var saved = await client.PutAsJsonAsync(
            Endpoint,
            new PreferenceBody("ocean", "dark"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var read = await client.GetFromJsonAsync<PreferenceBody>(
            Endpoint,
            TestContext.Current.CancellationToken);
        Assert.NotNull(read);
        Assert.Equal("ocean", read!.ColorScheme);
        Assert.Equal("dark", read.Mode);
    }

    [Fact]
    public async Task PutIsIdempotent()
    {
        // CA-ACC-03-03. Dos veces el mismo cuerpo: 200 las dos, y el estado final es el mismo.
        // Si el upsert insertara en vez de actualizar, la segunda llamada reventaría contra la
        // clave primaria compuesta.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedUserAsync(factory, SubjectId);
        using var client = CreateClient(factory, SubjectId, TenantId);

        var first = await client.PutAsJsonAsync(
            Endpoint,
            new PreferenceBody("indigo", "dark"),
            TestContext.Current.CancellationToken);
        var second = await client.PutAsJsonAsync(
            Endpoint,
            new PreferenceBody("indigo", "dark"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var read = await client.GetFromJsonAsync<PreferenceBody>(
            Endpoint,
            TestContext.Current.CancellationToken);
        Assert.Equal("indigo", read!.ColorScheme);
    }

    [Fact]
    public async Task PreferencesAreIsolatedPerTenant()
    {
        // CA-ACC-03-04, el criterio que protege la resolución de SDD-OD-17. La misma persona
        // en dos organizaciones tiene dos preferencias, y tocar una no puede mover la otra.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedUserAsync(factory, SubjectId);
        using var first = CreateClient(factory, SubjectId, TenantId);
        using var second = CreateClient(factory, SubjectId, OtherTenantId);

        await first.PutAsJsonAsync(
            Endpoint,
            new PreferenceBody("ocean", "dark"),
            TestContext.Current.CancellationToken);
        await second.PutAsJsonAsync(
            Endpoint,
            new PreferenceBody("graphite", "light"),
            TestContext.Current.CancellationToken);

        var fromFirst = await first.GetFromJsonAsync<PreferenceBody>(
            Endpoint,
            TestContext.Current.CancellationToken);
        var fromSecond = await second.GetFromJsonAsync<PreferenceBody>(
            Endpoint,
            TestContext.Current.CancellationToken);

        Assert.Equal("ocean", fromFirst!.ColorScheme);
        Assert.Equal("dark", fromFirst.Mode);
        Assert.Equal("graphite", fromSecond!.ColorScheme);
        Assert.Equal("light", fromSecond.Mode);
    }

    /// <summary>
    /// CA-ACC-03-05/06. <b>Sin tenant, por el camino del stub, la respuesta es `401`, no `403`</b>:
    /// `DevelopmentAuthenticationHandler:22` falla la autenticación cuando `X-Tenant-Id` no
    /// parsea, así que el request nunca llega al endpoint. El `403` que el endpoint devuelve
    /// pertenece al camino real —cookie de sesión más `ExternalClaimsTransformation`, donde una
    /// sesión válida sin membresía activa sí entra sin claim de tenant— y ese camino no es
    /// alcanzable con el stub. Queda cubierto por el runtime, no por esta prueba: escribir un
    /// `Assert` de `403` acá habría verificado una ficción.
    /// </summary>
    [Fact]
    public async Task WithoutTenantTheStubRejectsBeforeReachingTheEndpoint()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId);

        var read = await client.GetAsync(Endpoint, TestContext.Current.CancellationToken);
        var write = await client.PutAsJsonAsync(
            Endpoint,
            new PreferenceBody("ocean", "dark"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    [Fact]
    public async Task WithoutAnySessionItRespondsUnauthorized()
    {
        // CA-ACC-03-05.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Endpoint, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("botanical", "system")]
    [InlineData("botanical", "")]
    [InlineData("Botánica", "light")]
    [InlineData("scheme with spaces", "light")]
    [InlineData("", "light")]
    public async Task InvalidBodyIsRejected(string colorScheme, string mode)
    {
        // CA-ACC-03-07 y CA-ACC-03-08.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedUserAsync(factory, SubjectId);
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await client.PutAsJsonAsync(
            Endpoint,
            new PreferenceBody(colorScheme, mode),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownButWellFormedSchemeIsAccepted()
    {
        // CA-ACC-03-09. El catálogo de esquemas es del frontend: si el backend lo duplicara,
        // agregar un color pasaría a necesitar un deploy de la API.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await SeedUserAsync(factory, SubjectId);
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await client.PutAsJsonAsync(
            Endpoint,
            new PreferenceBody("midnight-2", "dark"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// La preferencia tiene FK a <c>identity.users</c>, y el subject que inventa el stub no
    /// existe en la base. Sin esto, todo <c>PUT</c> revienta contra la clave foránea con un
    /// `500` — que es exactamente lo que pasó la primera vez que corrieron estas pruebas.
    /// </summary>
    private static async Task SeedUserAsync(QepApiFactory factory, string userId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var identifier = new UserId(Guid.Parse(userId));
        if (await dbContext.Users.FindAsync(
                [identifier],
                TestContext.Current.CancellationToken) is not null)
        {
            return;
        }

        dbContext.Users.Add(User.CreateInvited(
            identifier,
            $"user-{identifier.Value:N}@example.com",
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
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
            // Fijado y no heredado, por SDD-CT-17: con el proveedor de correo de
            // appsettings.json y sus credenciales ausentes, el validador de opciones tira la
            // aplicación al arrancar y todas las pruebas mueren antes de su aserción.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
