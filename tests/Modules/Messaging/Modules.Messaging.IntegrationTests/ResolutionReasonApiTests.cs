using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-15: resolver con motivo opcional (§3.3), el motivo en el resumen y el historial (§3.5), la ingesta que reabre
/// lo limpia (§3.6) y el catálogo con su permiso (§3.4, §3.7).</summary>
public sealed class ResolutionReasonApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, long Version, HttpClient Client)
    {
        public string Url => ConversationUrl(Tenant.TenantId, ConversationId);
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.in", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(factory);
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var item = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0];
        return new Fixture(factory, connectionString, tenant, item.GetProperty("id").GetGuid(), item.GetProperty("version").GetInt64(), client);
    }

    private static Task<HttpResponseMessage> ResolveAsync(Fixture f, long version, object? body = null) =>
        SendAsync(f.Client, HttpMethod.Post, $"{f.Url}/resolve", body, ifMatch: $"\"{version}\"");

    /// <summary>Resuelve y devuelve la versión nueva; falla la prueba si no es 200.</summary>
    private static async Task<long> ResolveOkAsync(Fixture f, long version, object? body = null)
    {
        var response = await ResolveAsync(f, version, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetInt64();
    }

    private static async Task<long> ReopenOkAsync(Fixture f, long version)
    {
        var response = await SendAsync(f.Client, HttpMethod.Post, $"{f.Url}/reopen", ifMatch: $"\"{version}\"");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetInt64();
    }

    private static Task<long> ReasonCountAsync(Fixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.resolution_reasons WHERE tenant_id = @t", ("t", f.Tenant.TenantId));

    /// <summary>Sólo cuando la columna no es NULL: <c>ScalarAsync</c> de un NULL devuelve <c>DBNull</c> y el cast revienta.</summary>
    private static Task<string> CurrentReasonIdAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString, "SELECT resolution_reason_id::text FROM messaging.conversations WHERE id = @id", ("id", f.ConversationId));

    private static Task<long> ConversationsWithReasonAsync(Fixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations WHERE id = @id AND resolution_reason_id IS NOT NULL", ("id", f.ConversationId));

    // §3.3: sin cuerpo, {} y un nombre en blanco son «sin motivo», exactamente como antes de esta spec.
    [Fact]
    public async Task NoBodyAnEmptyObjectAndABlankNameResolveWithoutAReason()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var version = await ResolveOkAsync(f, f.Version);
        version = await ResolveOkAsync(f, await ReopenOkAsync(f, version), new { });
        await ResolveOkAsync(f, await ReopenOkAsync(f, version), new { reasonName = "   " });

        Assert.Equal(0L, await ReasonCountAsync(f));
        Assert.Equal(0L, await ConversationsWithReasonAsync(f));
        Assert.Equal(0L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE conversation_id = @id AND details ? 'reasonId'", ("id", f.ConversationId)));
    }

    [Fact]
    public async Task ANewNameCreatesTheReasonWithTheResolveAndItsAudit()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        await ResolveOkAsync(f, f.Version, new { reasonName = "  Venta cerrada " });

        var reasonId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.resolution_reasons WHERE name = 'Venta cerrada'");
        Assert.Equal(1L, await ReasonCountAsync(f));
        Assert.Equal(reasonId.ToString(), await CurrentReasonIdAsync(f));
        Assert.Equal(reasonId.ToString(), await ScalarAsync<string>(f.ConnectionString,
            "SELECT details->>'reasonId' FROM messaging.messages WHERE conversation_id = @id AND details->>'type' = 'Resolved'", ("id", f.ConversationId)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE source = 'messaging' AND resource_type = 'resolution_reason' AND action = 'messaging.resolution_reason.created' AND resource_id = @id",
            ("id", reasonId.ToString())));
    }

    // D-R4: el mismo nombre con otro caso o espacios reutiliza el motivo; ni fila nueva ni otra auditoría de created.
    [Fact]
    public async Task AnExistingNameIsReusedIgnoringCaseAndSurroundingSpaces()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var version = await ResolveOkAsync(f, f.Version, new { reasonName = "Venta cerrada" });
        await ResolveOkAsync(f, await ReopenOkAsync(f, version), new { reasonName = "  VENTA CERRADA  " });

        Assert.Equal(1L, await ReasonCountAsync(f));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(DISTINCT details->>'reasonId') FROM messaging.messages WHERE conversation_id = @id AND details->>'type' = 'Resolved'", ("id", f.ConversationId)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'messaging.resolution_reason.created'"));
    }

    [Fact]
    public async Task AReasonIdMustBeActiveInThisTenant()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var version = await ResolveOkAsync(f, f.Version, new { reasonName = "Spam" });
        version = await ReopenOkAsync(f, version);
        var spam = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.resolution_reasons WHERE name = 'Spam'");
        var foreign = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString,
            "INSERT INTO messaging.resolution_reasons (id, tenant_id, name, created_at, updated_at, version) VALUES (@id, @t, 'Ajeno', now(), now(), 1)",
            ("id", foreign), ("t", Guid.CreateVersion7()));

        var fromOtherTenant = await ResolveAsync(f, version, new { reasonId = foreign });
        var unknown = await ResolveAsync(f, version, new { reasonId = Guid.CreateVersion7() });
        var resolved = await ResolveAsync(f, version, new { reasonId = spam });
        version = await ReopenOkAsync(f, (await resolved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetInt64());
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.resolution_reasons SET deleted_at = now() WHERE id = @id", ("id", spam));
        var archived = await ResolveAsync(f, version, new { reasonId = spam });

        Assert.Equal(HttpStatusCode.NotFound, fromOtherTenant.StatusCode);
        Assert.Equal("messaging.resolution_reason.not_found", (await ProblemAsync(fromOtherTenant)).Code);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, archived.StatusCode);
        Assert.Equal("messaging.resolution_reason.not_found", (await ProblemAsync(archived)).Code);
    }

    // Review Focus 4: el borde de 60/61, y los dos campos a la vez. El mapa errors marca reasonName.
    [Fact]
    public async Task BothFieldsOrALongNameAreValidationErrorsOnReasonName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var both = await ResolveAsync(f, f.Version, new { reasonId = Guid.CreateVersion7(), reasonName = "Spam" });
        var tooLong = await ResolveAsync(f, f.Version, new { reasonName = new string('a', 61) });
        var sixty = await ResolveAsync(f, f.Version, new { reasonName = $"  {new string('a', 60)}  " });

        var (bothCode, bothKeys) = await ProblemAsync(both);
        var (longCode, longKeys) = await ProblemAsync(tooLong);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, both.StatusCode);
        Assert.Equal("validation.failed", bothCode);
        Assert.Equal(["reasonName"], bothKeys);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal("validation.failed", longCode);
        Assert.Equal(["reasonName"], longKeys);
        Assert.Equal(HttpStatusCode.OK, sixty.StatusCode);
    }

    // D-R9: el motivo 101 nuevo se rechaza; reutilizar uno de los 100 nunca choca con el tope.
    [Fact]
    public async Task TheHundredAndFirstNewReasonIsRejectedButReusingStillWorks()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ExecuteAsync(f.ConnectionString,
            "INSERT INTO messaging.resolution_reasons (id, tenant_id, name, created_at, updated_at, version) SELECT gen_random_uuid(), @t, 'Motivo ' || g, now(), now(), 1 FROM generate_series(1, 100) g",
            ("t", f.Tenant.TenantId));

        var full = await ResolveAsync(f, f.Version, new { reasonName = "Uno más" });
        var reused = await ResolveAsync(f, f.Version, new { reasonName = "motivo 7" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, full.StatusCode);
        Assert.Equal("messaging.resolution_reason.limit_reached", (await ProblemAsync(full)).Code);
        Assert.Equal(HttpStatusCode.OK, reused.StatusCode);
        Assert.Equal(100L, await ReasonCountAsync(f));
    }

    // D-R4: un resolver que falla no deja un motivo creado (una sola transacción).
    [Fact]
    public async Task AFailedResolveLeavesNoReasonBehind()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var stale = await ResolveAsync(f, f.Version + 7, new { reasonName = "Huérfano" });
        var version = await ResolveOkAsync(f, f.Version);
        var already = await ResolveAsync(f, version, new { reasonName = "Huérfano" });

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, already.StatusCode);
        Assert.Equal("messaging.conversation.already_resolved", (await ProblemAsync(already)).Code);
        Assert.Equal(0L, await ReasonCountAsync(f));
    }

    private static async Task<JsonElement> SummaryAsync(Fixture f) =>
        await f.Client.GetFromJsonAsync<JsonElement>(f.Url, Ct);

    private static async Task<JsonElement[]> ResolvedEventsAsync(Fixture f) =>
        (await f.Client.GetFromJsonAsync<JsonElement>(MessagesUrl(f.Tenant.TenantId, f.ConversationId), Ct))
            .GetProperty("items").EnumerateArray()
            .Where(item => item.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.Object && e.GetProperty("type").GetString() == "Resolved")
            .ToArray();

    // D-R7 y §3.5: el resumen trae el motivo mientras está resuelta; el evento del hilo lo trae siempre.
    [Fact]
    public async Task TheSummaryAndTheThreadEventCarryTheReason()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var response = await ResolveAsync(f, f.Version, new { reasonName = "Venta cerrada" });
        var resolvedSummary = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var detail = await SummaryAsync(f);
        var listed = (await f.Client.GetFromJsonAsync<JsonElement>($"{ConversationsUrl(f.Tenant.TenantId)}?status=Resolved", Ct)).GetProperty("items")[0];
        var resolvedEvent = Assert.Single(await ResolvedEventsAsync(f));

        foreach (var summary in new[] { resolvedSummary, detail, listed })
        {
            Assert.Equal("Venta cerrada", summary.GetProperty("resolutionReason").GetProperty("name").GetString());
        }

        Assert.Equal("Venta cerrada", resolvedEvent.GetProperty("event").GetProperty("reason").GetProperty("name").GetString());
        Assert.Equal(
            resolvedSummary.GetProperty("resolutionReason").GetProperty("id").GetGuid(),
            resolvedEvent.GetProperty("event").GetProperty("reason").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task WithoutAReasonBothAreNull()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var summary = await (await ResolveAsync(f, f.Version)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(JsonValueKind.Null, summary.GetProperty("resolutionReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, Assert.Single(await ResolvedEventsAsync(f)).GetProperty("event").GetProperty("reason").ValueKind);
    }

    // §3.6 y D-R2: reabrir —por API o por un entrante de la ingesta— limpia el motivo vigente; el evento viejo conserva el suyo.
    [Fact]
    public async Task ReopeningByApiOrByAnInboundClearsTheCurrentReasonButNotTheOldEvent()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var version = await ResolveOkAsync(f, f.Version, new { reasonName = "Spam" });

        await ReopenOkAsync(f, version);
        var afterApi = await SummaryAsync(f);
        await ResolveOkAsync(f, afterApi.GetProperty("version").GetInt64(), new { reasonName = "Venta cerrada" });
        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "wamid.2", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "otra cosa"));
        await DrainDeliveriesAsync(f.Factory);
        var afterInbound = await SummaryAsync(f);

        Assert.Equal(JsonValueKind.Null, afterApi.GetProperty("resolutionReason").ValueKind);
        Assert.Equal("Open", afterInbound.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, afterInbound.GetProperty("resolutionReason").ValueKind);
        Assert.Equal(0L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.conversations WHERE id = @id AND resolution_reason_id IS NOT NULL", ("id", f.ConversationId)));
        Assert.Equal(["Spam", "Venta cerrada"],
            (await ResolvedEventsAsync(f)).Select(item => item.GetProperty("event").GetProperty("reason").GetProperty("name").GetString()));
    }

    // D-R5: un motivo archivado sigue nombrado en el historial y en el resumen de la conversación que lo usa.
    [Fact]
    public async Task AnArchivedReasonIsStillNamedWhereItWasUsed()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await ResolveOkAsync(f, f.Version, new { reasonName = "Spam" });
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.resolution_reasons SET deleted_at = now()");

        Assert.Equal("Spam", (await SummaryAsync(f)).GetProperty("resolutionReason").GetProperty("name").GetString());
        Assert.Equal("Spam", Assert.Single(await ResolvedEventsAsync(f)).GetProperty("event").GetProperty("reason").GetProperty("name").GetString());
    }
}
