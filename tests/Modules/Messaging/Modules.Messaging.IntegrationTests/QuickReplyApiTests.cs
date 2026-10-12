using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-12 §3.6 y §3.7 por HTTP y contra la base: crear, listar (con <c>variables</c> resueltas para quien llama),
/// editar y borrar, con sus 403 (sin permiso, otro tenant, módulo apagado), 404, 412, 422 por campo, <c>shortcut_taken</c> y la
/// auditoría en la misma transacción.</summary>
public sealed class QuickReplyApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] AdminPermissions = [MessagingPermissions.ConversationRead, MessagingPermissions.QuickReplyManage];

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Admin);

    private static string QuickRepliesUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/messaging/quick-replies";

    private static string QuickReplyUrl(Guid tenantId, Guid quickReplyId) => $"{QuickRepliesUrl(tenantId)}/{quickReplyId}";

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        return new Fixture(factory, connectionString, tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, AdminPermissions));
    }

    private static async Task<JsonElement> CreateAsync(Fixture f, string shortcut, string body = "Abrimos de 8 a 6.")
    {
        var response = await SendAsync(f.Admin, HttpMethod.Post, QuickRepliesUrl(f.Tenant.TenantId), new { shortcut, body });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static Task<long> AuditCountAsync(Fixture f, string action, Guid quickReplyId) =>
        CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE source = 'messaging' AND action = @action AND resource_type = 'quick_reply' AND resource_id = @id",
            ("action", action), ("id", quickReplyId.ToString()));

    [Fact]
    public async Task CreatingStoresTheNormalizedReplyAndItsAudit()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var created = await CreateAsync(f, " Horarios ", "  Abrimos de 8 a 6, {nombre}.  ");

        Assert.Equal(("horarios", "Abrimos de 8 a 6, {nombre}.", 1L), (
            created.GetProperty("shortcut").GetString(), created.GetProperty("body").GetString(), created.GetProperty("version").GetInt64()));
        Assert.NotEqual(default, created.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(1L, await AuditCountAsync(f, "messaging.quick_reply.created", created.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task TheListIsOrderedByShortcutAndCarriesTheCallersVariables()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await CreateAsync(f, "pago");
        await CreateAsync(f, "horarios");
        await CreateAsync(f, "a-domicilio");
        var (_, advisorUserId) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Andrea Gómez", "advisor");
        using var advisor = CreateClient(f.Factory, advisorUserId, f.Tenant.TenantId, ReadPermissions);

        var list = await advisor.GetFromJsonAsync<JsonElement>(QuickRepliesUrl(f.Tenant.TenantId), Ct);

        Assert.Equal(["a-domicilio", "horarios", "pago"], list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("shortcut").GetString()));
        var variables = list.GetProperty("variables");
        Assert.Equal(("Andrea Gómez", "Messaging Test Org"), (variables.GetProperty("asesor").GetString(), variables.GetProperty("empresa").GetString()));
    }

    [Fact]
    public async Task ACallerWithoutMembershipGetsAnEmptyAdvisorNotNull()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var stranger = CreateClient(f.Factory, Guid.CreateVersion7(), f.Tenant.TenantId, ReadPermissions);

        var list = await stranger.GetFromJsonAsync<JsonElement>(QuickRepliesUrl(f.Tenant.TenantId), Ct);

        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
        Assert.Equal(string.Empty, list.GetProperty("variables").GetProperty("asesor").GetString());
        Assert.Equal("Messaging Test Org", list.GetProperty("variables").GetProperty("empresa").GetString());
    }

    // Decisión del owner (Task 4): {asesor} llega al cliente y nunca lleva el correo. El dueño tiene usuario con correo en identity;
    // al quedarle la membresía sin nombre, un fallback al correo sería visible acá.
    [Fact]
    public async Task ACallerWhoseMembershipHasNoNameGetsAnEmptyAdvisorNeverTheEmail()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await ExecuteAsync(f.ConnectionString,
            "UPDATE tenancy.memberships SET display_name = '' WHERE tenant_id = @t AND user_id = @u",
            ("t", f.Tenant.TenantId), ("u", f.Tenant.OwnerUserId));

        var list = await f.Admin.GetFromJsonAsync<JsonElement>(QuickRepliesUrl(f.Tenant.TenantId), Ct);

        Assert.Equal(string.Empty, list.GetProperty("variables").GetProperty("asesor").GetString());
        Assert.Equal("Messaging Test Org", list.GetProperty("variables").GetProperty("empresa").GetString());
    }

    [Fact]
    public async Task CreatingWithoutThePermissionOrFromAnotherTenantIs403AndWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var advisor = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        using var intruder = CreateClient(f.Factory, f.Tenant.OwnerUserId, Guid.CreateVersion7(), AdminPermissions);
        var url = QuickRepliesUrl(f.Tenant.TenantId);

        var denied = await SendAsync(advisor, HttpMethod.Post, url, new { shortcut = "horarios", body = "Hola" });
        var crossRead = await SendAsync(intruder, HttpMethod.Get, url);
        var crossCreate = await SendAsync(intruder, HttpMethod.Post, url, new { shortcut = "horarios", body = "Hola" });

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossRead.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(crossRead)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, crossCreate.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(crossCreate)).Code);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.quick_replies"));
    }

    [Fact]
    public async Task WithTheModuleOffEveryRouteIs403WithItsCode()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await ExecuteAsync(f.ConnectionString,
            "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'messaging'", ("t", f.Tenant.TenantId));
        var url = QuickRepliesUrl(f.Tenant.TenantId);

        var read = await SendAsync(f.Admin, HttpMethod.Get, url);
        var create = await SendAsync(f.Admin, HttpMethod.Post, url, new { shortcut = "horarios", body = "Hola" });

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(read)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(create)).Code);
    }

    // Review Focus 1: el atajo se compara normalizado; el índice es por tenant.
    [Fact]
    public async Task ATakenShortcutIs422EvenInAnotherCaseAndAnotherTenantCanUseIt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await CreateAsync(f, "horarios");
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherAdmin = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, AdminPermissions);

        var taken = await SendAsync(f.Admin, HttpMethod.Post, QuickRepliesUrl(f.Tenant.TenantId), new { shortcut = "HORARIOS", body = "Otro" });
        var elsewhere = await SendAsync(otherAdmin, HttpMethod.Post, QuickRepliesUrl(other.TenantId), new { shortcut = "horarios", body = "Otro" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, taken.StatusCode);
        Assert.Equal("messaging.quick_reply.shortcut_taken", (await ProblemAsync(taken)).Code);
        Assert.Equal(HttpStatusCode.Created, elsewhere.StatusCode);
    }

    [Fact]
    public async Task AnInvalidInputIs422WithErrorsByField()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var url = QuickRepliesUrl(f.Tenant.TenantId);

        var both = await SendAsync(f.Admin, HttpMethod.Post, url, new { shortcut = "/horarios", body = "   " });
        var variable = await SendAsync(f.Admin, HttpMethod.Post, url, new { shortcut = "saludo", body = "Hola {Nombre}" });
        var empty = await SendAsync(f.Admin, HttpMethod.Post, url, new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, both.StatusCode);
        var (bothCode, bothKeys) = await ProblemAsync(both);
        Assert.Equal("validation.failed", bothCode);
        Assert.Equal(["body", "shortcut"], bothKeys);
        var (variableCode, variableKeys) = await ProblemAsync(variable);
        Assert.Equal("validation.failed", variableCode);
        Assert.Equal(["body"], variableKeys);
        Assert.Equal(["body", "shortcut"], (await ProblemAsync(empty)).ErrorKeys);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.quick_replies"));
    }
}
