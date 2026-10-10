using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §8.4 y §9: take/transfer/release con If-Match (428/412), sus eventos y auditoría, la
/// carrera de dos take (RF4), transferir a quien no puede responder (RF5), resolver no libera.</summary>
public sealed class AssignmentApiTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, Guid Owner, Guid Beatriz, Guid BeatrizUser, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using (var anonymous = factory.CreateClient())
        {
            await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", null, "wamid.1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
            await DrainDeliveriesAsync(factory);
        }

        var (beatriz, beatrizUser) = await SeedMemberAsync(connectionString, tenant.TenantId, "Beatriz", "advisor");
        var conversationId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations");
        return new Fixture(factory, connectionString, tenant, conversationId, await OwnerMembershipIdAsync(connectionString, tenant), beatriz, beatrizUser,
            CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions));
    }

    private static string ActionUrl(Fixture f, string action) => $"{ConversationUrl(f.Tenant.TenantId, f.ConversationId)}/{action}";

    private static Task<long> VersionAsync(Fixture f) => ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

    private static Task<HttpResponseMessage> ActAsync(Fixture f, HttpClient client, string action, long version, object? body = null) =>
        SendAsync(client, HttpMethod.Post, ActionUrl(f, action), body, $"\"{version}\"");

    private static Task<long> AuditAsync(Fixture f, string action) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = @a", ("a", action));

    [Fact]
    public async Task TakingAnswersTheSummaryAndTakingSomeoneElsesRecordsThePrevious()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var beatriz = CreateClient(f.Factory, f.BeatrizUser, f.Tenant.TenantId, ManagePermissions);

        var mine = await ActAsync(f, f.Client, "take", await VersionAsync(f));
        var hers = await ActAsync(f, beatriz, "take", await VersionAsync(f));

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        var body = await hers.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal((f.Beatriz, true), (body.GetProperty("assignedTo").GetProperty("memberId").GetGuid(), body.GetProperty("assignedTo").GetProperty("isMe").GetBoolean()));
        Assert.Equal($"Taken|{f.Beatriz}|{f.Owner}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT details->>'type' || '|' || (details->>'actor') || '|' || (details->>'previous') FROM messaging.messages WHERE details->>'type' = 'Taken' AND details ? 'previous'"));
        Assert.Equal(2L, await AuditAsync(f, "messaging.conversation.taken"));
    }

    // RF4 y §9.1: las dos mandan la misma versión; una commitea y la otra recibe 412.
    [Fact]
    public async Task TwoTakesWithTheSameVersionAnswerOne200AndOne412()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var beatriz = CreateClient(f.Factory, f.BeatrizUser, f.Tenant.TenantId, ManagePermissions);
        var version = await VersionAsync(f);

        var responses = await Task.WhenAll(ActAsync(f, f.Client, "take", version), ActAsync(f, beatriz, "take", version));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.PreconditionFailed], responses.Select(response => response.StatusCode).Order());
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE details->>'type' = 'Taken'"));
    }

    [Fact]
    public async Task WithoutIfMatchIs428AndAStaleVersionIs412()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var missing = await SendAsync(f.Client, HttpMethod.Post, ActionUrl(f, "take"));
        var stale = await ActAsync(f, f.Client, "release", await VersionAsync(f) - 1);

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task TransferringToAnAdvisorAssignsAndRecordsTheEventAndAudit()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var response = await ActAsync(f, f.Client, "transfer", await VersionAsync(f), new { memberId = f.Beatriz });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(f.Beatriz, await ScalarAsync<Guid>(f.ConnectionString, "SELECT assigned_member_id FROM messaging.conversations"));
        Assert.Equal($"{f.Owner}|{f.Beatriz}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT (details->>'actor') || '|' || (details->>'target') FROM messaging.messages WHERE details->>'type' = 'Transferred'"));
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.transferred"));
    }

    // RF5 y §11: sin manage, de otro tenant o removida responden lo mismo y no confirman nada.
    [Fact]
    public async Task TransferringToAMemberThatCannotReplyIs422WithoutConfirmingWhy()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var other = await RegisterTenantAsync(f.Factory);
        var (billing, _) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Carlos", "billing");
        var (foreign, _) = await SeedMemberAsync(f.ConnectionString, other.TenantId, "Elena", "advisor");
        var (removed, _) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Diana", "advisor", state: "Removed");
        var version = await VersionAsync(f);

        foreach (var memberId in new[] { billing, foreign, removed, Guid.CreateVersion7() })
        {
            var response = await ActAsync(f, f.Client, "transfer", version, new { memberId });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("messaging.conversation.assignee_cannot_reply", (await ProblemAsync(response)).Code);
        }

        Assert.Equal(version, await VersionAsync(f));
    }

    [Fact]
    public async Task TransferWithoutMemberIdIsAValidationErrorOnMemberId()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var response = await ActAsync(f, f.Client, "transfer", await VersionAsync(f), new { memberId = (Guid?)null });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal(("validation.failed", "memberId"), (problem.Code, Assert.Single(problem.ErrorKeys)));
    }

    [Fact]
    public async Task ReleasingClearsTheAssigneeAndReleasingAgainIs200WithoutABump()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ActAsync(f, f.Client, "take", await VersionAsync(f));

        var released = await ActAsync(f, f.Client, "release", await VersionAsync(f));
        var version = await VersionAsync(f);
        var again = await ActAsync(f, f.Client, "release", version);

        Assert.Equal(JsonValueKind.Null, (await released.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("assignedTo").ValueKind);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(version, await VersionAsync(f));
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.released"));
    }

    // Spec 2026-10-10 §2, decisión 3: resolver no libera; el evento lleva quién resolvió (P9).
    [Fact]
    public async Task ResolvingKeepsTheAssigneeAndRecordsWhoResolved()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ActAsync(f, f.Client, "take", await VersionAsync(f));

        var resolved = await ActAsync(f, f.Client, "resolve", await VersionAsync(f));

        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal(f.Owner, await ScalarAsync<Guid>(f.ConnectionString, "SELECT assigned_member_id FROM messaging.conversations"));
        Assert.Equal(f.Owner.ToString(), await ScalarAsync<string>(f.ConnectionString,
            "SELECT details->>'actor' FROM messaging.messages WHERE details->>'type' = 'Resolved'"));
    }

    [Fact]
    public async Task ACallerWithoutAnActiveMembershipIs403()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var stranger = CreateClient(f.Factory, Guid.CreateVersion7(), f.Tenant.TenantId, ManagePermissions);

        var response = await ActAsync(f, stranger, "take", await VersionAsync(f));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(response)).Code);
    }

    // Decisión del controlador: transferir también vale con la conversación resuelta (resolver no libera).
    [Fact]
    public async Task TransferringAResolvedConversationIsAllowedAndKeepsItResolved()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ActAsync(f, f.Client, "resolve", await VersionAsync(f));

        var response = await ActAsync(f, f.Client, "transfer", await VersionAsync(f), new { memberId = f.Beatriz });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{f.Beatriz}|Resolved", await ScalarAsync<string>(f.ConnectionString,
            "SELECT assigned_member_id::text || '|' || status FROM messaging.conversations"));
    }
}
