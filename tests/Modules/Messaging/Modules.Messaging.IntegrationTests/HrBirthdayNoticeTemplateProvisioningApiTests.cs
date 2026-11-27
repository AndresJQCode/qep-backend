using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>HR birthday notices, calqued on <see cref="TaskNoticeTemplateProvisioningApiTests"/>: provisioning creates a body-only PENDING
/// <c>HrBirthdayNotice</c> row that the screen shows in <c>hrBirthdayNoticeTemplate</c>; repeating is <c>hr_birthday_notice_exists</c>; the draft
/// carries the base text with its four variables; and everything needs <c>messaging.template.manage</c>.</summary>
public sealed class HrBirthdayNoticeTemplateProvisioningApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Admin);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var created = 880;
        factory.MetaHandler.RespondWith("/222/message_templates", request => request.Method == HttpMethod.Post
            ? FakeMetaGraphHandler.Json(HttpStatusCode.OK, $$"""{"id":"{{Interlocked.Increment(ref created)}}","status":"PENDING","category":"UTILITY"}""")
            : FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"data":[]}"""));
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, TemplatePermissions));
    }

    private static string NoticeUrl(Fixture f) => $"{TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId)}/hr-birthday-notice";

    private static MetaRequest[] MetaCalls(Fixture f) =>
        f.Factory.MetaHandler.Requests.Where(request => request.Uri!.AbsolutePath.StartsWith("/v24.0/", StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task ProvisionCreatesPendingTemplate()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Admin, HttpMethod.Post, NoticeUrl(f));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("qep_cumpleanos_equipo", "es", "UTILITY", "PENDING", "HrBirthdayNotice", "881"), (
            created.GetProperty("name").GetString(), created.GetProperty("language").GetString(), created.GetProperty("category").GetString(),
            created.GetProperty("status").GetString(), created.GetProperty("kind").GetString(), created.GetProperty("metaTemplateId").GetString()));
        Assert.Equal(["nombre_contacto", "hoy", "fecha", "proximos"], created.GetProperty("parameters").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(HrBirthdayNoticeTemplateDefinition.BaseBody, created.GetProperty("body").GetString());
        Assert.Equal("[]", await ScalarAsync<string>(f.ConnectionString,
            "SELECT changed_fields::text FROM audit.entries WHERE action = 'messaging.template.hr_birthday_notice_provisioned' AND resource_id = @id",
            ("id", created.GetProperty("id").GetGuid().ToString())));
    }

    [Fact]
    public async Task SecondProvisionIs422Exists()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(f.Admin, HttpMethod.Post, NoticeUrl(f))).StatusCode);
        var before = MetaCalls(f).Length;

        var response = await SendAsync(f.Admin, HttpMethod.Post, NoticeUrl(f));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.template.hr_birthday_notice_exists", (await ProblemAsync(response)).Code);
        Assert.Equal(before, MetaCalls(f).Length);
    }

    [Fact]
    public async Task DraftAnswersTheBaseBody()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var draft = await f.Admin.GetFromJsonAsync<JsonElement>($"{NoticeUrl(f)}/draft", Ct);

        Assert.Equal(HrBirthdayNoticeTemplateDefinition.BaseBody, draft.GetProperty("body").GetString());
        Assert.False(draft.GetProperty("locked").GetBoolean());
        Assert.Equal(["nombre_contacto", "hoy", "fecha", "proximos"],
            draft.GetProperty("variables").EnumerateArray().Select(variable => variable.GetProperty("key").GetString()));

        var rejected = await SendAsync(f.Admin, HttpMethod.Post, NoticeUrl(f), new { body = "Hola {{nombre_contacto}}, {{edad}}." });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        var (code, keys) = await ProblemAsync(rejected);
        Assert.Equal(("validation.failed", "body"), (code, Assert.Single(keys)));
    }

    [Fact]
    public async Task TemplateManagementCarriesTheStatus()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var created = await (await SendAsync(f.Admin, HttpMethod.Post, NoticeUrl(f))).Content.ReadFromJsonAsync<JsonElement>(Ct);
        await ExecuteAsync(f.ConnectionString,
            "INSERT INTO messaging.template_mirror_syncs (tenant_id, connection_id, synced_at) VALUES (@t, @c, now())", ("t", f.Tenant.TenantId), ("c", f.ConnectionId));

        var screen = await f.Admin.GetFromJsonAsync<JsonElement>(TemplateManagementUrl(f.Tenant.TenantId, f.ConnectionId), Ct);

        Assert.Equal("PENDING", screen.GetProperty("hrBirthdayNoticeTemplate").GetProperty("status").GetString());
        Assert.Equal(created.GetProperty("id").GetGuid(), screen.GetProperty("hrBirthdayNoticeTemplate").GetProperty("templateId").GetGuid());
        Assert.Equal(JsonValueKind.Null, screen.GetProperty("taskNoticeTemplate").ValueKind);
    }

    [Fact]
    public async Task WithoutTemplateManageIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var manager = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(manager, HttpMethod.Post, NoticeUrl(f))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"{NoticeUrl(f)}/draft", Ct)).StatusCode);
    }
}
