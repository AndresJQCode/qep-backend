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

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid tenantId, Guid id, string shortcut, string body, long? version) =>
        SendAsync(client, HttpMethod.Put, QuickReplyUrl(tenantId, id), new { shortcut, body, version });

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid tenantId, Guid id, long? version) =>
        SendAsync(client, HttpMethod.Delete, version is null ? QuickReplyUrl(tenantId, id) : $"{QuickReplyUrl(tenantId, id)}?version={version}");

    // Review Focus 4: el mismo atajo que ya tiene no choca con su propia fila.
    [Fact]
    public async Task UpdatingKeepsItsOwnShortcutBumpsTheVersionAndAudits()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f, "horarios")).GetProperty("id").GetGuid();

        var response = await PutAsync(f.Admin, f.Tenant.TenantId, id, "horarios", "  Abrimos de 7 a 7, {nombre}.  ", 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal((id, "horarios", "Abrimos de 7 a 7, {nombre}.", 2L), (
            updated.GetProperty("id").GetGuid(), updated.GetProperty("shortcut").GetString(), updated.GetProperty("body").GetString(), updated.GetProperty("version").GetInt64()));
        Assert.Equal(1L, await AuditCountAsync(f, "messaging.quick_reply.updated", id));
    }

    [Fact]
    public async Task RenamingToATakenShortcutIs422()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await CreateAsync(f, "horarios");
        var id = (await CreateAsync(f, "pago")).GetProperty("id").GetGuid();

        var taken = await PutAsync(f.Admin, f.Tenant.TenantId, id, "Horarios", "Otro", 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, taken.StatusCode);
        Assert.Equal("messaging.quick_reply.shortcut_taken", (await ProblemAsync(taken)).Code);
        Assert.Equal("pago", await ScalarAsync<string>(f.ConnectionString, "SELECT shortcut FROM messaging.quick_replies WHERE id = @id", ("id", id)));
    }

    // Review Focus 5: la versión vieja no edita ni borra.
    [Fact]
    public async Task AStaleVersionIs412OnUpdateAndOnDeleteAndNothingChanges()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f, "horarios")).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(f.Admin, f.Tenant.TenantId, id, "horarios", "Versión dos.", 1)).StatusCode);

        var staleUpdate = await PutAsync(f.Admin, f.Tenant.TenantId, id, "horarios", "Pisada.", 1);
        var staleDelete = await DeleteAsync(f.Admin, f.Tenant.TenantId, id, 1);

        Assert.Equal(HttpStatusCode.PreconditionFailed, staleUpdate.StatusCode);
        Assert.Equal("concurrency.conflict", (await ProblemAsync(staleUpdate)).Code);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleDelete.StatusCode);
        Assert.Equal("concurrency.conflict", (await ProblemAsync(staleDelete)).Code);
        Assert.Equal("Versión dos.", await ScalarAsync<string>(f.ConnectionString, "SELECT body FROM messaging.quick_replies WHERE id = @id", ("id", id)));
        Assert.Equal(2L, await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.quick_replies WHERE id = @id", ("id", id)));
    }

    [Fact]
    public async Task AMissingVersionIs422OnVersion()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f, "horarios")).GetProperty("id").GetGuid();

        var update = await PutAsync(f.Admin, f.Tenant.TenantId, id, "horarios", "Otro", null);
        var delete = await DeleteAsync(f.Admin, f.Tenant.TenantId, id, null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, update.StatusCode);
        Assert.Equal(["version"], (await ProblemAsync(update)).ErrorKeys);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, delete.StatusCode);
        Assert.Equal(["version"], (await ProblemAsync(delete)).ErrorKeys);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.quick_replies"));
    }

    [Fact]
    public async Task DeletingRemovesItAndAudits()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f, "horarios")).GetProperty("id").GetGuid();

        var deleted = await DeleteAsync(f.Admin, f.Tenant.TenantId, id, 1);
        var list = await f.Admin.GetFromJsonAsync<JsonElement>(QuickRepliesUrl(f.Tenant.TenantId), Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
        Assert.Equal(1L, await AuditCountAsync(f, "messaging.quick_reply.deleted", id));
    }

    // Review Focus 5: el id de otro tenant bajo la ruta propia es 404, igual que uno inexistente; la ruta ajena es 403.
    [Fact]
    public async Task AnUnknownOrForeignIdIs404AndAnotherTenantsRouteIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f, "horarios")).GetProperty("id").GetGuid();
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherAdmin = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, AdminPermissions);
        var unknown = Guid.CreateVersion7();

        var notFound = new[]
        {
            await PutAsync(f.Admin, f.Tenant.TenantId, unknown, "x-y", "Hola", 1),
            await DeleteAsync(f.Admin, f.Tenant.TenantId, unknown, 1),
            await PutAsync(otherAdmin, other.TenantId, id, "x-y", "Hola", 1),
            await DeleteAsync(otherAdmin, other.TenantId, id, 1),
        };
        var forbidden = new[]
        {
            await PutAsync(otherAdmin, f.Tenant.TenantId, id, "x-y", "Hola", 1),
            await DeleteAsync(otherAdmin, f.Tenant.TenantId, id, 1),
        };

        foreach (var response in notFound)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("messaging.quick_reply.not_found", (await ProblemAsync(response)).Code);
        }

        foreach (var response in forbidden)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("authorization.denied", (await ProblemAsync(response)).Code);
        }

        Assert.Equal(("horarios", 1L), (
            await ScalarAsync<string>(f.ConnectionString, "SELECT shortcut FROM messaging.quick_replies WHERE id = @id", ("id", id)),
            await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.quick_replies WHERE id = @id", ("id", id))));
    }

    [Fact]
    public async Task UpdatingOrDeletingWithoutThePermissionIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f, "horarios")).GetProperty("id").GetGuid();
        using var advisor = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(advisor, f.Tenant.TenantId, id, "horarios", "Otro", 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(advisor, f.Tenant.TenantId, id, 1)).StatusCode);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.quick_replies WHERE version = 1"));
    }
}
