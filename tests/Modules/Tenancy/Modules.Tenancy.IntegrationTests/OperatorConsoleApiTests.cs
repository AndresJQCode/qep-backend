using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using static Modules.Tenancy.IntegrationTests.TenantModulesApiTests;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// La consola de operador de punta a punta (spec 2026-10-08) con el stub de desarrollo. El tenant
/// operador es simulado (sin fila en tenancy.tenants): el stub no le enmascara módulos y el filtro
/// de operador lo reconoce por configuración. Los tenants administrados sí se registran por la API.
/// </summary>
public sealed class OperatorConsoleApiTests
{
    private static readonly Guid OperatorTenantId = Guid.Parse("01900000-0000-7000-8000-00000000c0de");

    private static readonly string[] AllOperatorPermissions =
        ["operator.tenants.read", "operator.modules.manage", "operator.tenants.manage"];

    // CA1861: el cuerpo del rol personalizado va en un campo, no como matriz literal.
    private static readonly string[] OnlyTenantsRead = ["operator.tenants.read"];

    [Fact]
    public async Task TheStubKeepsOperatorPermissionsOnlyInTheOperatorTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var otherTenantId = Guid.CreateVersion7();
        using var inOperator = StubClient(
            factory, Guid.CreateVersion7(), OperatorTenantId, "operator.tenants.read", "tenancy.settings.read");
        using var elsewhere = StubClient(
            factory, Guid.CreateVersion7(), otherTenantId, "operator.tenants.read", "tenancy.settings.read");

        Assert.Contains("operator.tenants.read", await EffectivePermissionsAsync(inOperator, OperatorTenantId));
        Assert.Equal(["tenancy.settings.read"], await EffectivePermissionsAsync(elsewhere, otherTenantId));
    }

    // Spec 2026-10-08 §2: en el stub el filtro corre siempre, también cuando el tenant tiene fila y
    // pasa por el enmascarado de módulos.
    [Fact]
    public async Task TheStubDropsOperatorPermissionsInARegisteredNonOperatorTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var client = StubClient(factory, ownerId, tenantId, "operator.tenants.read", "tenancy.settings.read");

        var permissions = await EffectivePermissionsAsync(client, tenantId);

        Assert.DoesNotContain("operator.tenants.read", permissions);
        Assert.Contains("tenancy.settings.read", permissions);
    }

    // Spec «Errores y casos borde»: sin la clave, nadie es operador.
    [Fact]
    public async Task WithoutAnOperatorConfiguredTheStubDropsThemEverywhere()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = StubClient(factory, Guid.CreateVersion7(), OperatorTenantId, AllOperatorPermissions);

        Assert.Empty(await EffectivePermissionsAsync(client, OperatorTenantId));
    }

    // Spec 2026-10-08 §2 + D11 (decisión P1 del plan): permissions nunca trae operator.*; roles[]
    // lo oculta fuera del tenant operador.
    [Fact]
    public async Task TheCatalogNeverOffersOperatorCheckboxesAndHidesThemFromOtherAdmins()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var otherTenantId = Guid.CreateVersion7();

        var inOperator = await CatalogAsync(factory, OperatorTenantId);
        var elsewhere = await CatalogAsync(factory, otherTenantId);

        Assert.DoesNotContain(inOperator.Permissions, p => p.Permission.StartsWith("operator.", StringComparison.Ordinal));
        Assert.DoesNotContain(elsewhere.Permissions, p => p.Permission.StartsWith("operator.", StringComparison.Ordinal));
        Assert.Contains("operator.tenants.read", inOperator.Roles.Single(r => r.Role == "admin").Permissions);
        Assert.DoesNotContain(elsewhere.Roles.Single(r => r.Role == "admin").Permissions,
            p => p.StartsWith("operator.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRolesListHidesOperatorPermissionsOutsideTheOperatorTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var otherTenantId = Guid.CreateVersion7();

        Assert.Contains("operator.modules.manage", (await AdminRoleAsync(factory, OperatorTenantId)).Permissions);
        Assert.DoesNotContain((await AdminRoleAsync(factory, otherTenantId)).Permissions,
            p => p.StartsWith("operator.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACustomRoleCannotCarryOperatorPermissionsEvenInTheOperatorTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        using var client = StubClient(factory, Guid.CreateVersion7(), OperatorTenantId, "advisorship.roles.manage");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{OperatorTenantId}/authorization/roles",
            new { key = "operador", displayName = "Operador", description = "", permissions = OnlyTenantsRead },
            TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "authorization.role.permission_operator_only");
    }

    // Spec 2026-10-08 §4: el stub consulta el estado con ITenantDirectory, no con ITenantModules.
    [Fact]
    public async Task TheStubEmitsNoTenantClaimForASuspendedTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var client = StubClient(factory, ownerId, tenantId, "tenancy.settings.read");
        Assert.Contains("tenancy.settings.read", await EffectivePermissionsAsync(client, tenantId));

        await SetTenantStatusAsync(factory, tenantId, suspend: true);

        var me = await client.GetAsync($"/api/v1/tenants/{tenantId}/authorization/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, me.StatusCode);
        var modules = await client.GetAsync($"/api/v1/tenants/{tenantId}/modules", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, modules.StatusCode);
    }

    // Por el agregado, igual que en RealAuthenticationApiTests (cada clase de pruebas lleva sus helpers).
    internal static async Task SetTenantStatusAsync(QepApiFactory factory, Guid tenantId, bool suspend)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new TenantId(tenantId);
        var tenant = await dbContext.Tenants.SingleAsync(value => value.Id == id, TestContext.Current.CancellationToken);
        if (suspend)
        {
            tenant.Suspend(ChangeReason.Nonpayment, DateTimeOffset.UtcNow);
        }
        else
        {
            tenant.Reactivate(ChangeReason.Correction, DateTimeOffset.UtcNow);
        }

        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<CatalogPayload> CatalogAsync(QepApiFactory factory, Guid tenantId)
    {
        using var client = StubClient(factory, Guid.CreateVersion7(), tenantId, "advisorship.read");
        var catalog = await client.GetFromJsonAsync<CatalogPayload>(
            $"/api/v1/tenants/{tenantId}/authorization/catalog", TestContext.Current.CancellationToken);
        Assert.NotNull(catalog);
        return catalog;
    }

    private static async Task<RolePayload> AdminRoleAsync(QepApiFactory factory, Guid tenantId)
    {
        using var client = StubClient(factory, Guid.CreateVersion7(), tenantId, "advisorship.read");
        var roles = await client.GetFromJsonAsync<List<RolePayload>>(
            $"/api/v1/tenants/{tenantId}/authorization/roles", TestContext.Current.CancellationToken);
        Assert.NotNull(roles);
        return roles.Single(role => role.Role == "admin");
    }

    internal static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(code, problem?.Code);
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

    private sealed record CatalogPayload(string CatalogVersion, List<CatalogRole> Roles, List<CatalogPermission> Permissions);
    private sealed record CatalogRole(string Role, string[] Permissions);
    private sealed record CatalogPermission(string Permission);
    private sealed record RolePayload(string Role, string[] Permissions);
    internal sealed record ProblemPayload(string Code);

    [Fact]
    public async Task AnOperatorListsTenantsWithTheSummaryAndSearches()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (first, _) = await RegisterAsync(factory);
        await RegisterAsync(factory);
        using var client = OperatorClient(factory);

        var all = await GetOkAsync<TenantPagePayload>(client, Url("tenants"));
        Assert.Equal(2, all.Total);
        Assert.Equal(new SummaryPayload(2, 0, 0), all.Summary);
        Assert.All(all.Items, item => Assert.Equal((6, 7, "Active", false), (item.ActiveModules, item.TotalModules, item.Status, item.IsOperator)));

        var slug = (await GetOkAsync<DetailPayload>(client, Url($"tenants/{first}"))).Slug;
        var searched = await GetOkAsync<TenantPagePayload>(client, Url($"tenants?search={slug}"));
        Assert.Equal(first, Assert.Single(searched.Items).TenantId);
        Assert.Equal(1, searched.Total);
        Assert.Equal(2, searched.Summary.Total);   // el resumen no se filtra

        // Review Focus 2: el comodín es literal.
        Assert.Equal(0, (await GetOkAsync<TenantPagePayload>(client, Url("tenants?search=%25"))).Total);
        // Review Focus 4: más allá de la última página, vacío con el total intacto.
        var beyond = await GetOkAsync<TenantPagePayload>(client, Url("tenants?page=5&pageSize=1"));
        Assert.Empty(beyond.Items);
        Assert.Equal(2, beyond.Total);
    }

    // Contrato con la SPA (se construye en paralelo): nombres exactos de §5.
    [Fact]
    public async Task TheDetailJsonMatchesTheContract()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, _) = await RegisterAsync(factory);
        using var client = OperatorClient(factory);

        using var json = System.Text.Json.JsonDocument.Parse(
            await client.GetStringAsync(Url($"tenants/{tenantId}"), TestContext.Current.CancellationToken));
        var root = json.RootElement;
        Assert.Equal(
            ["tenantId", "slug", "displayName", "createdAt", "status", "statusChangedAt", "statusReason", "version", "isOperator", "modules"],
            root.EnumerateObject().Select(property => property.Name));
        var pos = root.GetProperty("modules")[6];
        Assert.Equal(
            ["key", "status", "enabled", "dependencies", "since", "source", "lastReason"],
            pos.EnumerateObject().Select(property => property.Name));
        Assert.Equal("none", pos.GetProperty("status").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, pos.GetProperty("since").ValueKind);
        Assert.Equal("Active", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task AnUnknownTargetIsNotFoundForTheOperator()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        using var client = OperatorClient(factory);

        await AssertProblemAsync(
            await client.GetAsync(Url($"tenants/{Guid.CreateVersion7()}"), TestContext.Current.CancellationToken),
            HttpStatusCode.NotFound, "tenancy.tenant.not_found");
    }

    // Spec «Errores y casos borde»: el permiso inyectado por X-Permissions en otro tenant no alcanza.
    [Fact]
    public async Task ANonOperatorTenantIsForbiddenEvenWithThePermissionInjected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var otherTenantId = Guid.CreateVersion7();
        using var client = StubClient(factory, Guid.CreateVersion7(), otherTenantId, AllOperatorPermissions);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{otherTenantId}/operator/tenants", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Doble capa: la política pasa (el claim es el operador) y el handler ve que la ruta no coincide.
    [Fact]
    public async Task TheRouteTenantMustMatchTheClaim()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        using var client = OperatorClient(factory);

        await AssertProblemAsync(
            await client.GetAsync($"/api/v1/tenants/{Guid.CreateVersion7()}/operator/tenants", TestContext.Current.CancellationToken),
            HttpStatusCode.Forbidden, "authorization.denied");
    }

    [Fact]
    public async Task OutOfRangePagingIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        using var client = OperatorClient(factory);

        await AssertProblemAsync(
            await client.GetAsync(Url("tenants?pageSize=0"), TestContext.Current.CancellationToken),
            HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    [Fact]
    public async Task DeactivatingReportingWritesRowHistoryAndAuditAndMasksOnTheNextRequest()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, ownerId) = await RegisterAsync(factory);
        using var operatorClient = OperatorClient(factory);
        using var member = StubClient(factory, ownerId, tenantId, "reporting.all_advisors.read", "tenancy.settings.read");
        Assert.Contains("reporting.all_advisors.read", await EffectivePermissionsAsync(member, tenantId));

        var detail = await PostChangesAsync(operatorClient, tenantId, "cancellation", "No lo usan", ("reporting", "inactive"));

        var reporting = detail.Modules.Single(module => module.Key == "reporting");
        Assert.Equal(("inactive", false, "cancellation"), (reporting.Status, reporting.Enabled, reporting.LastReason));
        Assert.DoesNotContain("reporting.all_advisors.read", await EffectivePermissionsAsync(member, tenantId));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var id = new TenantId(tenantId);
            var change = await dbContext.TenantChanges.AsNoTracking().SingleAsync(value => value.TenantId == id, TestContext.Current.CancellationToken);
            Assert.Equal(("active", "inactive", "No lo usan"), (change.FromStatus, change.ToStatus, change.Note));
            var audited = await dbContext.Database.SqlQuery<string>(
                $"SELECT changed_fields::text AS \"Value\" FROM audit.entries WHERE action = 'tenancy.tenant_modules.changed' AND tenant_id = {tenantId}")
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Contains("reporting:active->inactive", audited, StringComparison.Ordinal);
        }

        await PostChangesAsync(operatorClient, tenantId, "courtesy", null, ("reporting", "active"));
        Assert.Contains("reporting.all_advisors.read", await EffectivePermissionsAsync(member, tenantId));
    }

    [Fact]
    public async Task AnInconsistentBatchIsRejectedAndChangesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, _) = await RegisterAsync(factory);
        using var client = OperatorClient(factory);

        await AssertProblemAsync(
            await client.PostAsJsonAsync(Url($"tenants/{tenantId}/modules/changes"),
                new { changes = new[] { new { key = "customers", status = "inactive" } }, reason = "cancellation" },
                TestContext.Current.CancellationToken),
            HttpStatusCode.UnprocessableEntity, "tenancy.modules.inconsistent_dependencies");
        var detail = await GetOkAsync<DetailPayload>(client, Url($"tenants/{tenantId}"));
        Assert.All(detail.Modules.Where(module => module.Key != "pos"), module => Assert.Equal("active", module.Status));
    }

    [Fact]
    public async Task ActivatingPosCreatesTheRowFromTheConsole()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, _) = await RegisterAsync(factory);
        using var client = OperatorClient(factory);

        var pos = (await PostChangesAsync(client, tenantId, "contract", null, ("pos", "active")))
            .Modules.Single(module => module.Key == "pos");

        Assert.Equal(("active", true, "operator"), (pos.Status, pos.Enabled, pos.Source));
    }

    [Theory]
    [InlineData("""{"changes":[{"key":"inventory","status":"active"}],"reason":"contract"}""")]
    [InlineData("""{"changes":[null],"reason":"contract"}""")]
    [InlineData("""{"changes":[{"key":"POS","status":"active"}],"reason":"contract"}""")]
    public async Task AnUnknownShapeIsValidationFailedNot500(string body)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, _) = await RegisterAsync(factory);
        using var client = OperatorClient(factory);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        await AssertProblemAsync(
            await client.PostAsync(Url($"tenants/{tenantId}/modules/changes"), content, TestContext.Current.CancellationToken),
            HttpStatusCode.UnprocessableEntity, "validation.failed");
    }

    [Fact]
    public async Task ReadingIsNotEnoughToChangeModules()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, _) = await RegisterAsync(factory);
        using var client = StubClient(factory, Guid.CreateVersion7(), OperatorTenantId, "operator.tenants.read");

        var response = await client.PostAsJsonAsync(Url($"tenants/{tenantId}/modules/changes"),
            new { changes = new[] { new { key = "pos", status = "active" } }, reason = "contract" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Review Focus 3: A activa quotations y B desactiva customers a la vez. Cualquiera sea el orden,
    // uno gana y el otro choca con la regla; nunca queda quotations activo sin customers.
    [Fact]
    public async Task TwoConcurrentBatchesLeaveAConsistentState()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var (tenantId, _) = await RegisterAsync(factory);
        using var first = OperatorClient(factory);
        using var second = OperatorClient(factory);
        await PostChangesAsync(first, tenantId, "cancellation", null, ("quotations", "inactive"), ("orders", "inactive"));

        var responses = await Task.WhenAll(
            first.PostAsJsonAsync(Url($"tenants/{tenantId}/modules/changes"),
                new { changes = new[] { new { key = "quotations", status = "active" } }, reason = "contract" }, TestContext.Current.CancellationToken),
            second.PostAsJsonAsync(Url($"tenants/{tenantId}/modules/changes"),
                new { changes = new[] { new { key = "customers", status = "inactive" } }, reason = "cancellation" }, TestContext.Current.CancellationToken));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.UnprocessableEntity));
        var modules = (await GetOkAsync<DetailPayload>(first, Url($"tenants/{tenantId}"))).Modules.ToDictionary(module => module.Key);
        Assert.False(modules["quotations"].Status == "active" && modules["customers"].Status != "active");
    }

    private static async Task<DetailPayload> PostChangesAsync(
        HttpClient client, Guid tenantId, string reason, string? note, params (string Key, string Status)[] changes)
    {
        var response = await client.PostAsJsonAsync(
            Url($"tenants/{tenantId}/modules/changes"),
            new { changes = changes.Select(change => new { key = change.Key, status = change.Status }).ToArray(), reason, note },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<DetailPayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    private static HttpClient OperatorClient(QepApiFactory factory, Guid? subjectId = null) =>
        StubClient(factory, subjectId ?? Guid.CreateVersion7(), OperatorTenantId, AllOperatorPermissions);

    private static string Url(string path) => $"/api/v1/tenants/{OperatorTenantId}/operator/{path}";

    private static async Task<T> GetOkAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<T>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    private sealed record TenantPagePayload(List<TenantItemPayload> Items, int Total, int Page, int PageSize, SummaryPayload Summary);
    private sealed record TenantItemPayload(Guid TenantId, string Slug, string DisplayName, string Status, DateTimeOffset CreatedAt, int ActiveModules, int TotalModules, bool IsOperator);
    private sealed record SummaryPayload(int Total, int WithoutModules, int Inactive);
    private sealed record DetailPayload(Guid TenantId, string Slug, string DisplayName, DateTimeOffset CreatedAt, string Status, DateTimeOffset? StatusChangedAt, string? StatusReason, long Version, bool IsOperator, List<ModulePayload> Modules);
    private sealed record ModulePayload(string Key, string Status, bool Enabled, string[] Dependencies, DateTimeOffset? Since, string? Source, string? LastReason);
}
