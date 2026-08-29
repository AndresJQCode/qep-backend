using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Tenancy.Application;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// Los roles que define un tenant, por HTTP.
/// </summary>
/// <remarks>
/// Las guardas ya tienen pruebas unitarias contra dobles. Estas cubren lo que aquéllas no
/// pueden: que el endpoint esté mapeado, que su policy exista —se registra a mano y por
/// separado— y que el código de dominio llegue como el status correcto y no como un 500.
/// </remarks>
public sealed class RoleApiTests
{
    private const string SubjectId = "01900000-0000-7000-8000-00000000a001";
    private const string TenantId = "01900000-0000-7000-8000-00000000a002";

    // Estaticos y no literales en linea: CA1861 se queja de una matriz constante que se pasa
    // repetidas veces a un metodo que no la muta.
    private static readonly string[] ReadProducts = ["catalog.product.read"];
    private static readonly string[] ManageProducts = ["catalog.product.manage"];

    private static string RolesUrl() =>
        $"/api/v1/tenants/{TenantId}/authorization/roles";

    [Fact]
    public async Task ListReturnsTheSystemRolesMarkedAsSuch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = Reader(factory);

        var response = await client.GetAsync(RolesUrl(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var roles = await response.Content.ReadFromJsonAsync<RolePayload[]>(
            TestContext.Current.CancellationToken);
        var admin = Assert.Single(roles!, role => role.Role == "admin");
        Assert.True(admin.IsSystem);
        // Sin fila propia: un rol de sistema se identifica por su clave, que es lo que viaja
        // en la membresía. Un id inventado haría creer que se puede pedir por id.
        Assert.Null(admin.Id);
    }

    [Fact]
    public async Task ListNeedsOnlyTheReadPermission()
    {
        // Leer no exige el permiso de escritura: quien asigna roles a una persona necesita ver
        // qué concede cada uno, y esa capacidad es `advisorship.manage`.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory, SubjectId, TenantId, TenancyPermissions.AdvisorshipRead);

        var response = await client.GetAsync(RolesUrl(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateStoresTheRoleAndTheListShowsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = Manager(factory);

        var created = await CreateAsync(client, "ventas-junior", "Ventas junior");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var role = await created.Content.ReadFromJsonAsync<RolePayload>(
            TestContext.Current.CancellationToken);
        Assert.False(role!.IsSystem);
        Assert.NotNull(role.Id);

        var list = await client.GetAsync(RolesUrl(), TestContext.Current.CancellationToken);
        var roles = await list.Content.ReadFromJsonAsync<RolePayload[]>(
            TestContext.Current.CancellationToken);
        Assert.Contains(roles!, item => item.Role == "ventas-junior" && !item.IsSystem);
    }

    /// <summary>
    /// `advisorship.manage` no alcanza para definir roles.
    /// </summary>
    /// <remarks>
    /// Es la separación que evita una escalada trivial: quien administra miembros puede
    /// nombrar asesoras, y si eso alcanzara para reescribir lo que una asesora puede hacer,
    /// podría concederse cualquier permiso del sistema sin pasar por nadie.
    ///
    /// Por HTTP y no sólo en el handler porque la policy de `advisorship.roles.manage` se
    /// registra **a mano y por separado** de su `PermissionDefinition`: sin una llamada real,
    /// una policy ausente pasa desapercibida.
    /// </remarks>
    [Fact]
    public async Task ManagingMembersIsNotEnoughToDefineRoles()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(
            factory,
            SubjectId,
            TenantId,
            TenancyPermissions.AdvisorshipRead,
            TenancyPermissions.AdvisorshipManage);

        var response = await CreateAsync(client, "ventas-junior", "Ventas junior");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateWithAPermissionOutsideTheCatalogIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = Manager(factory);

        var response = await CreateAsync(
            client, "inventado", "Inventado", "catalog.product.inventado");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("authorization.role.permission_unknown", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateWithASystemRoleKeyIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = Manager(factory);

        var response = await CreateAsync(client, "admin", "Mi admin");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("authorization.role.key_reserved", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateWithADuplicateKeyIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = Manager(factory);
        await CreateAsync(client, "ventas-junior", "Ventas junior");

        var response = await CreateAsync(client, "ventas-junior", "Otro nombre");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("authorization.role.key_taken", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateWithoutIfMatchIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = Manager(factory);
        var role = await CreatedRoleAsync(client, "ventas-junior");

        using var request = new HttpRequestMessage(
            HttpMethod.Patch, $"{RolesUrl()}/{role.Id}")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Ventas senior",
                description = string.Empty,
                permissions = ReadProducts,
            }),
        };
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Sin precondición no se escribe: dos personas editando el mismo rol es una carrera
        // real, y perderla en silencio concede o quita accesos que nadie decidió.
        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Fact]
    public async Task UpdateWithAStaleVersionConflicts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = Manager(factory);
        var role = await CreatedRoleAsync(client, "ventas-junior");

        var response = await UpdateAsync(client, role.Id!.Value, "Otro", 99);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task UpdateReplacesPermissionsAndReturnsTheNewVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = Manager(factory);
        var role = await CreatedRoleAsync(client, "ventas-junior");

        var response = await UpdateAsync(
            client, role.Id!.Value, "Ventas senior", role.Version, "catalog.product.manage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<RolePayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("Ventas senior", updated!.DisplayName);
        Assert.Equal(ManageProducts, updated.Permissions);
        Assert.True(updated.Version > role.Version);
        // El ETag viaja para que el próximo PATCH tenga qué mandar en If-Match sin releer.
        Assert.Equal($"\"{updated.Version}\"", response.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task DeleteRemovesARoleNobodyHas()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = Manager(factory);
        var role = await CreatedRoleAsync(client, "ventas-junior");

        var response = await client.DeleteAsync(
            $"{RolesUrl()}/{role.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var list = await client.GetAsync(RolesUrl(), TestContext.Current.CancellationToken);
        var roles = await list.Content.ReadFromJsonAsync<RolePayload[]>(
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(roles!, item => item.Role == "ventas-junior");
    }

    [Fact]
    public async Task AnUnknownRoleIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = Manager(factory);

        var response = await client.DeleteAsync(
            $"{RolesUrl()}/{Guid.CreateVersion7()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ARoleOfAnotherTenantIsNotReachable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var owner = Manager(factory);
        var role = await CreatedRoleAsync(owner, "ventas-junior");

        const string OtherTenant = "01900000-0000-7000-8000-00000000a0ff";
        using var intruder = CreateClient(
            factory,
            "01900000-0000-7000-8000-00000000a0fe",
            OtherTenant,
            TenancyPermissions.AdvisorshipRead,
            TenancyPermissions.AdvisorshipRolesManage);

        var response = await intruder.DeleteAsync(
            $"/api/v1/tenants/{OtherTenant}/authorization/roles/{role.Id}",
            TestContext.Current.CancellationToken);

        // No encontrado, no prohibido: decir "existe pero no es tuyo" filtra que existe.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- helpers ----

    private static HttpClient Reader(QepApiFactory factory) =>
        CreateClient(factory, SubjectId, TenantId, TenancyPermissions.AdvisorshipRead);

    private static HttpClient Manager(QepApiFactory factory) =>
        CreateClient(
            factory,
            SubjectId,
            TenantId,
            TenancyPermissions.AdvisorshipRead,
            TenancyPermissions.AdvisorshipRolesManage);

    private static Task<HttpResponseMessage> CreateAsync(
        HttpClient client,
        string key,
        string displayName,
        params string[] permissions) =>
        client.PostAsJsonAsync(
            RolesUrl(),
            new
            {
                key,
                displayName,
                description = string.Empty,
                permissions = permissions.Length == 0 ? ReadProducts : permissions,
            },
            TestContext.Current.CancellationToken);

    private static async Task<RolePayload> CreatedRoleAsync(HttpClient client, string key)
    {
        var response = await CreateAsync(client, key, key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RolePayload>(
            TestContext.Current.CancellationToken))!;
    }

    private static async Task<HttpResponseMessage> UpdateAsync(
        HttpClient client,
        Guid roleId,
        string displayName,
        long version,
        params string[] permissions)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch, $"{RolesUrl()}/{roleId}")
        {
            Content = JsonContent.Create(new
            {
                displayName,
                description = string.Empty,
                permissions = permissions.Length == 0 ? ReadProducts : permissions,
            }),
        };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
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

    private static HttpClient CreateClient(
        QepApiFactory factory,
        string subjectId,
        string tenantId,
        params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    private sealed record RolePayload(
        Guid? Id,
        string Role,
        string DisplayName,
        string Description,
        string Category,
        string? RiskLevel,
        IReadOnlyCollection<string> Permissions,
        bool IsSystem,
        long Version);

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
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
