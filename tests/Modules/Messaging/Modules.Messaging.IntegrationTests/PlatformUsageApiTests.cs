using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Reporte de uso de la plataforma (spec 2026-11-01) contra el esquema migrado: <c>PlatformUsageSource</c> lee por nombre
/// <c>tenancy.tenants</c>, <c>messaging.messages</c> y <c>messaging.conversations</c>, y esta clase es la que lo amarra. El
/// tenant operador es simulado (sin fila en <c>tenancy.tenants</c>); los administrados se registran por la API.
/// </summary>
public sealed class PlatformUsageApiTests
{
    private static readonly Guid OperatorTenantId = Guid.Parse("01900000-0000-7000-8000-00000000c0de");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId);

    private static string UsageUrl(string query = "") => $"/api/v1/tenants/{OperatorTenantId}/operator/usage{query}";

    private static async Task<Fixture> ArrangeAsync(QepApiFactory factory, string connectionString, string connectionName = "Ventas", string phoneNumberId = "111")
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, connectionName, phoneNumberId, phoneNumberId + "9");
        return new Fixture(factory, connectionString, tenant, connectionId);
    }

    private static HttpClient OperatorClient(QepApiFactory factory) =>
        CreateClient(factory, Guid.CreateVersion7(), OperatorTenantId, "operator.usage.read");

    private static async Task<JsonElement> ReadAsync(QepApiFactory factory, string query = "")
    {
        using var client = OperatorClient(factory);
        var response = await client.GetAsync(UsageUrl(query), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static JsonElement TenantRow(JsonElement report, Guid tenantId) =>
        report.GetProperty("tenants").EnumerateArray().Single(row => row.GetProperty("tenantId").GetGuid() == tenantId);

    private static (int WhatsApp, int Instagram, int Total) Counts(JsonElement row)
    {
        var conversations = row.GetProperty("conversations");
        return (conversations.GetProperty("whatsApp").GetInt32(), conversations.GetProperty("instagram").GetInt32(), conversations.GetProperty("total").GetInt32());
    }

    private static async Task<Guid> SeedMessageAsync(
        Fixture f, Guid conversationId, short direction, DateTimeOffset createdAt, bool imported = false, short kind = 1,
        short? sentByKind = null, Guid? connectionId = null)
    {
        var id = Guid.CreateVersion7();
        var isEvent = direction == 3;
        await ExecuteAsync(
            f.ConnectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, details, wamid, client_id, sent_by_kind, created_at, imported)
            VALUES (@id, @conversationId, @tenantId, @connectionId, @createdAt, @direction, @kind, @status, @text, @details::jsonb, @wamid, @clientId, @sentByKind, @createdAt, @imported)
            """,
            ("id", id), ("conversationId", conversationId), ("tenantId", f.Tenant.TenantId), ("connectionId", connectionId ?? f.ConnectionId),
            ("createdAt", createdAt), ("direction", direction), ("kind", isEvent ? (short)13 : kind), ("status", isEvent ? (short)2 : (short)1),
            ("text", isEvent ? DBNull.Value : "texto"), ("details", isEvent ? "{\"type\":\"Assigned\"}" : DBNull.Value),
            ("wamid", direction == 1 ? $"wamid.{id:N}" : DBNull.Value),
            ("clientId", direction == 2 ? Guid.CreateVersion7() : DBNull.Value),
            ("sentByKind", (object?)sentByKind ?? DBNull.Value), ("imported", imported));
        return id;
    }

    private static Task<Guid> SeedInboundAsync(Fixture f, Guid conversationId, DateTimeOffset? at = null, bool imported = false) =>
        SeedMessageAsync(f, conversationId, 1, at ?? DateTimeOffset.UtcNow, imported);

    private static async Task<Guid> SeedInstagramConversationAsync(Fixture f, string instagramUserId)
    {
        var connectionId = await SeedInstagramConnectionAsync(f.Factory, f.Tenant.TenantId, "Instagram", instagramUserId);
        using var scope = f.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var conversation = Conversation.StartWithUserId(
            Guid.CreateVersion7(), f.Tenant.TenantId, connectionId, instagramUserId, null, "Laura", DateTimeOffset.UtcNow, ConversationChannel.Instagram);
        dbContext.Conversations.Add(conversation);
        await dbContext.SaveChangesAsync(Ct);
        return conversation.Id;
    }

    [Fact]
    public async Task AnInboundCountsItsConversationOnce()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var f = await ArrangeAsync(factory, database.GetConnectionString());
        var conversationId = await SeedConversationAsync(factory, f.Tenant.TenantId, f.ConnectionId, "573001234567");
        await SeedInboundAsync(f, conversationId);
        await SeedInboundAsync(f, conversationId);
        await SeedInboundAsync(f, conversationId);

        var report = await ReadAsync(factory);

        Assert.Equal((1, 0, 1), Counts(TenantRow(report, f.Tenant.TenantId)));
        Assert.Equal("month", report.GetProperty("period").GetString());
    }

    [Fact]
    public async Task AnOutboundWithoutInboundDoesNotCount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var f = await ArrangeAsync(factory, database.GetConnectionString());
        var campaign = await SeedConversationAsync(factory, f.Tenant.TenantId, f.ConnectionId, "573001110001");
        var api = await SeedConversationAsync(factory, f.Tenant.TenantId, f.ConnectionId, "573001110002");
        var advisor = await SeedConversationAsync(factory, f.Tenant.TenantId, f.ConnectionId, "573001110003");
        // Una campaña no lleva autor; la clave de API (4) y el asesor (1) sí.
        await SeedMessageAsync(f, campaign, 2, DateTimeOffset.UtcNow);
        await SeedMessageAsync(f, api, 2, DateTimeOffset.UtcNow, sentByKind: 4);
        await SeedMessageAsync(f, advisor, 2, DateTimeOffset.UtcNow, sentByKind: 1);

        var report = await ReadAsync(factory);

        Assert.Equal((0, 0, 0), Counts(TenantRow(report, f.Tenant.TenantId)));
    }

    [Fact]
    public async Task AnImportedInboundDoesNotCount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var f = await ArrangeAsync(factory, database.GetConnectionString());
        var conversationId = await SeedConversationAsync(factory, f.Tenant.TenantId, f.ConnectionId, "573001234567");
        await SeedInboundAsync(f, conversationId, imported: true);

        var report = await ReadAsync(factory);

        Assert.Equal((0, 0, 0), Counts(TenantRow(report, f.Tenant.TenantId)));
    }

    [Fact]
    public async Task AnEventRowDoesNotCount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var f = await ArrangeAsync(factory, database.GetConnectionString());
        var conversationId = await SeedConversationAsync(factory, f.Tenant.TenantId, f.ConnectionId, "573001234567");
        await SeedMessageAsync(f, conversationId, 3, DateTimeOffset.UtcNow);

        var report = await ReadAsync(factory);

        Assert.Equal((0, 0, 0), Counts(TenantRow(report, f.Tenant.TenantId)));
    }

    [Fact]
    public async Task AnOldConversationWithANewInboundCounts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var f = await ArrangeAsync(factory, database.GetConnectionString());
        var conversationId = await SeedConversationAsync(factory, f.Tenant.TenantId, f.ConnectionId, "573001234567");
        // La conversación nació hace 90 días y tiene un entrante viejo; hoy llegó otro: cuenta por el de hoy, no por su antigüedad.
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET created_at = now() - interval '90 days' WHERE id = @id", ("id", conversationId));
        await SeedInboundAsync(f, conversationId, DateTimeOffset.UtcNow.AddDays(-90));
        await SeedInboundAsync(f, conversationId);

        var report = await ReadAsync(factory, "?period=day");

        Assert.Equal((1, 0, 1), Counts(TenantRow(report, f.Tenant.TenantId)));
    }

    [Fact]
    public async Task AnInboundBeforeThePeriodDoesNotCount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var f = await ArrangeAsync(factory, database.GetConnectionString());
        var conversationId = await SeedConversationAsync(factory, f.Tenant.TenantId, f.ConnectionId, "573001234567");
        // Dos días atrás queda antes del inicio de "hoy" en cualquier hora del día.
        await SeedInboundAsync(f, conversationId, DateTimeOffset.UtcNow.AddDays(-2));

        var report = await ReadAsync(factory, "?period=day");

        Assert.Equal((0, 0, 0), Counts(TenantRow(report, f.Tenant.TenantId)));
        Assert.Equal("day", report.GetProperty("period").GetString());
    }

    [Fact]
    public async Task ChannelsAreSeparated()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var f = await ArrangeAsync(factory, database.GetConnectionString());
        var whatsApp1 = await SeedConversationAsync(factory, f.Tenant.TenantId, f.ConnectionId, "573001110001");
        var whatsApp2 = await SeedConversationAsync(factory, f.Tenant.TenantId, f.ConnectionId, "573001110002");
        var instagram = await SeedInstagramConversationAsync(f, "17841400000000001");
        await SeedInboundAsync(f, whatsApp1);
        await SeedInboundAsync(f, whatsApp2);
        await SeedMessageAsync(f, instagram, 1, DateTimeOffset.UtcNow);

        var report = await ReadAsync(factory);

        Assert.Equal((2, 1, 3), Counts(TenantRow(report, f.Tenant.TenantId)));
    }

    [Fact]
    public async Task AnInstagramConversationCountsAfterItsConnectionIsDeleted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var f = await ArrangeAsync(factory, database.GetConnectionString());
        var instagram = await SeedInstagramConversationAsync(f, "17841400000000001");
        await SeedMessageAsync(f, instagram, 1, DateTimeOffset.UtcNow);
        await ExecuteAsync(f.ConnectionString, "DELETE FROM integrations.connections WHERE tenant_id = @tenantId", ("tenantId", f.Tenant.TenantId));

        var report = await ReadAsync(factory);

        Assert.Equal((0, 1, 1), Counts(TenantRow(report, f.Tenant.TenantId)));
    }

    [Fact]
    public async Task ATenantAtZeroStays()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var quiet = await ArrangeAsync(factory, database.GetConnectionString());
        var busy = await ArrangeAsync(factory, database.GetConnectionString(), "Soporte", "333");
        var conversationId = await SeedConversationAsync(factory, busy.Tenant.TenantId, busy.ConnectionId, "573001234567");
        await SeedInboundAsync(busy, conversationId);

        var report = await ReadAsync(factory);

        Assert.Equal((0, 0, 0), Counts(TenantRow(report, quiet.Tenant.TenantId)));
        Assert.Equal((1, 0, 1), Counts(TenantRow(report, busy.Tenant.TenantId)));
        Assert.Equal(busy.Tenant.TenantId, report.GetProperty("tenants")[0].GetProperty("tenantId").GetGuid());
        Assert.Equal(2, report.GetProperty("registered").GetProperty("month").GetInt32());
    }

    [Fact]
    public async Task ConnectedMembersComeWithTheirNames()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var f = await ArrangeAsync(factory, database.GetConnectionString());
        var zoe = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Zoe", "advisor");
        var ana = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Ana", "advisor");
        var presence = factory.Services.GetRequiredService<IMessagingPresence>();
        presence.Connected("c1", f.Tenant.TenantId, zoe.MembershipId);
        presence.Connected("c2", f.Tenant.TenantId, ana.MembershipId);
        // Una segunda pestaña de Ana no la repite.
        presence.Connected("c3", f.Tenant.TenantId, ana.MembershipId);

        var report = await ReadAsync(factory);

        var connected = TenantRow(report, f.Tenant.TenantId).GetProperty("connected").EnumerateArray().ToArray();
        Assert.Equal(["Ana", "Zoe"], connected.Select(member => member.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(ana.MembershipId, connected[0].GetProperty("memberId").GetGuid());
    }

    [Fact]
    public async Task ARemovedMemberStaysAsMiembroEliminado()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var f = await ArrangeAsync(factory, database.GetConnectionString());
        var removedId = Guid.CreateVersion7();
        factory.Services.GetRequiredService<IMessagingPresence>().Connected("c1", f.Tenant.TenantId, removedId);

        var report = await ReadAsync(factory);

        var connected = TenantRow(report, f.Tenant.TenantId).GetProperty("connected").EnumerateArray().Single();
        Assert.Equal(removedId, connected.GetProperty("memberId").GetGuid());
        Assert.Equal("Miembro eliminado", connected.GetProperty("name").GetString());
    }

    [Fact]
    public async Task AnUnknownTenantInPresenceIsIgnored()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var f = await ArrangeAsync(factory, database.GetConnectionString());
        var ghostTenantId = Guid.CreateVersion7();
        factory.Services.GetRequiredService<IMessagingPresence>().Connected("c1", ghostTenantId, Guid.CreateVersion7());

        var report = await ReadAsync(factory);

        var ids = report.GetProperty("tenants").EnumerateArray().Select(row => row.GetProperty("tenantId").GetGuid()).ToArray();
        Assert.Contains(f.Tenant.TenantId, ids);
        Assert.DoesNotContain(ghostTenantId, ids);
    }

    [Fact]
    public async Task AClientTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        var f = await ArrangeAsync(factory, database.GetConnectionString());
        using var client = CreateClient(factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, "operator.usage.read");

        var response = await client.GetAsync($"/api/v1/tenants/{f.Tenant.TenantId}/operator/usage", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WithoutThePermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        using var client = CreateClient(factory, Guid.CreateVersion7(), OperatorTenantId, "operator.tenants.read");

        var response = await client.GetAsync(UsageUrl(), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownPeriodIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        using var client = OperatorClient(factory);

        var response = await client.GetAsync(UsageUrl("?period=year"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, errorKeys) = await ProblemAsync(response);
        Assert.Equal("validation.failed", code);
        Assert.Contains("Period", errorKeys);
    }
}
