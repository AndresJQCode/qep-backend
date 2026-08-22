using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// Cubre la superficie de autorización que la SPA lee para decidir qué renderizar.
///
/// El endpoint del catálogo ya existía; <c>/authorization/me</c> lo agrega AUTH-04
/// porque nada exponía los permisos *efectivos* del llamador: el catálogo devuelve las
/// definiciones de rol y permiso, y la respuesta de sesión lleva sólo usuario, email y
/// tenants. Sin eso un cliente sólo puede descubrir qué le está permitido intentándolo y
/// leyendo el 403 — que no puede ocultar una acción antes de que se intente. Ver SDD-OD-10.
/// </summary>
public sealed class AuthorizationCatalogApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    private static readonly string[] ReadOnlyPermissions =
        ["advisorship.read", "tenancy.settings.read"];

    private static readonly string[] UnknownPermissionOnly = ["none.at.all"];

    private sealed record EffectivePermissionsResponse(
        Guid TenantId,
        Guid UserId,
        IReadOnlyCollection<string> Permissions);

    [Fact]
    public async Task EffectivePermissionsReturnsWhatTheCallerActuallyHas()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        client.DefaultRequestHeaders.Add(
            "X-Permissions",
            "advisorship.read,tenancy.settings.read");

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/authorization/me",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<EffectivePermissionsResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(Guid.Parse(TenantId), body!.TenantId);
        Assert.Equal(Guid.Parse(SubjectId), body.UserId);
        Assert.Equal(ReadOnlyPermissions, body.Permissions);
    }

    /// <summary>
    /// Preguntar "qué puedo hacer acá" no tiene que requerir un permiso en sí mismo: exigir uno
    /// vuelve la respuesta inalcanzable justo para los sujetos cuya respuesta es "casi nada",
    /// que es el caso que la UI más necesita renderizar bien.
    /// </summary>
    [Fact]
    public async Task EffectivePermissionsNeedsNoPermissionOfItsOwn()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", "none.at.all");

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/authorization/me",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<EffectivePermissionsResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(UnknownPermissionOnly, body!.Permissions);
    }

    [Fact]
    public async Task EffectivePermissionsRejectsAnotherTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        // Autenticado para OtherTenant, preguntando por el tenant sembrado.
        using var client = CreateClient(factory, OtherSubjectId, OtherTenantId);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/authorization/me",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EffectivePermissionsRejectsAnonymous()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/authorization/me",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
