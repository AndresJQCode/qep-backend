using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Npgsql;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// <c>ops/2026-10-30-repair-history-direction.sql</c>: las filas que el parser viejo importó como entrantes porque esperaba un
/// <c>to</c> que Meta no manda en el historial. Las filas se siembran a mano tal como las dejó ese parser, y la entrega lleva la forma
/// real del payload. Por decisión del owner el script no toca <c>status</c> ni <c>unread_count</c> de la conversación.
/// </summary>
public sealed class HistoryDirectionRepairTests
{
    private const string PhoneNumberId = "111";
    private const string ManagedWaId = "573000000001";
    private const string AdvisorWaId = "573000000002";
    private const string SettledWaId = "573000000003";
    private const string RollbackStatement = "\nROLLBACK;";

    private const short Inbound = 1;
    private const short Outbound = 2;
    private const short Sent = 1;
    private const short Delivered = 2;
    private const short Read = 3;

    private static readonly DateTimeOffset Base = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(string ConnectionString, Guid TenantId, Guid ConnectionId);

    private static string ScriptPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "ops", "2026-10-30-repair-history-direction.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"The repair script was not found above {AppContext.BaseDirectory}.");
    }

    private static string CommittingScript()
    {
        var script = File.ReadAllText(ScriptPath());
        Assert.Equal(2, script.Split(RollbackStatement).Length);
        return script.Replace(RollbackStatement, "\nCOMMIT;", StringComparison.Ordinal);
    }

    private static async Task<Dictionary<string, long>> RunAsync(string connectionString, string script)
    {
        var results = new Dictionary<string, long>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(script, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        do
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                results.Add(reader.GetString(0), reader.GetInt64(1));
            }
        }
        while (await reader.NextResultAsync(TestContext.Current.CancellationToken));

        return results;
    }

    private static async Task<Guid> InsertConversationAsync(Fixture f, string waId, string status, int unread, bool managed, DateTimeOffset? lastInboundAt)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            f.ConnectionString,
            """
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, channel, wa_id, status, unread_count, last_activity_at,
                                                 history_managed, imported, started_by_echo, created_at, updated_at, version, last_inbound_at)
            VALUES (@id, @tenantId, @connectionId, 'WhatsApp', @waId, @status, @unread, @activity, @managed, true, false, @activity, @activity, 1,
                    CASE WHEN @hasInbound THEN @lastInbound END)
            """,
            ("id", id), ("tenantId", f.TenantId), ("connectionId", f.ConnectionId), ("waId", waId), ("status", status), ("unread", unread),
            ("activity", Base), ("managed", managed), ("hasInbound", lastInboundAt is not null), ("lastInbound", lastInboundAt ?? Base));
        return id;
    }

    private static async Task<Guid> InsertMessageAsync(
        Fixture f, Guid conversationId, string wamid, DateTimeOffset occurredAt, short direction, short status, bool imported)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            f.ConnectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, wamid, created_at, imported)
            VALUES (@id, @conversationId, @tenantId, @connectionId, @occurredAt, @direction, 1, @status, @wamid, @wamid, @occurredAt, @imported)
            """,
            ("id", id), ("conversationId", conversationId), ("tenantId", f.TenantId), ("connectionId", f.ConnectionId),
            ("occurredAt", occurredAt), ("direction", direction), ("status", status), ("wamid", wamid), ("imported", imported));
        return id;
    }

    private static Task<int> InsertEventAsync(Fixture f, Guid conversationId, DateTimeOffset occurredAt) =>
        ExecuteAsync(
            f.ConnectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, details, created_at)
            VALUES (@id, @conversationId, @tenantId, @connectionId, @occurredAt, 3, 13, 2, '{"type":"Assigned"}'::jsonb, @occurredAt)
            """,
            ("id", Guid.CreateVersion7()), ("conversationId", conversationId), ("tenantId", f.TenantId), ("connectionId", f.ConnectionId), ("occurredAt", occurredAt));

    private static Task<int> SetSummaryAsync(Fixture f, Guid conversationId, Guid messageId, DateTimeOffset occurredAt, short direction, short status) =>
        ExecuteAsync(
            f.ConnectionString,
            """
            UPDATE messaging.conversations SET last_message_id = @messageId, last_message_direction = @direction, last_message_kind = 1,
                   last_message_preview = 'vista previa', last_message_status = @status, last_message_at = @occurredAt
             WHERE id = @conversationId
            """,
            ("messageId", messageId), ("direction", direction), ("status", status), ("occurredAt", occurredAt), ("conversationId", conversationId));

    private static Task<int> InsertDeliveryAsync(Fixture f, string payload) =>
        ExecuteAsync(
            f.ConnectionString,
            """
            INSERT INTO messaging.webhook_deliveries (body_sha256, payload, received_at, attempts, processed_at)
            VALUES (@hash, @payload::jsonb, now(), 1, now())
            """,
            ("hash", RandomNumberGenerator.GetBytes(32)), ("payload", payload));

    /// <summary>La entrega real: los mensajes del negocio con su número en <c>from</c>, sin <c>to</c>; uno sin <c>from</c>.</summary>
    private static string RealShapedPayload()
    {
        var timestamp = Base.ToUnixTimeSeconds();
        var node = JsonNode.Parse(MetaPayloads.History(
            PhoneNumberId, 0, 1, 50,
            (ManagedWaId, [("wamid.managed.in", "Hola", timestamp, false), ("wamid.managed.out", "Te ayudo", timestamp + 60, true)]),
            (AdvisorWaId, [("wamid.advisor.out", "Ya salió", timestamp, true)]),
            (SettledWaId,
            [
                ("wamid.settled.out", "¿Te sirve?", timestamp, true),
                ("wamid.settled.in", "Sí", timestamp, false),
                ("wamid.settled.live", "Lo que entró en vivo", timestamp - 60, true),
                ("wamid.settled.nofrom", "Sin autor", timestamp - 120, false),
            ])))!;
        var messages = node["entry"]![0]!["changes"]![0]!["value"]!["history"]![0]!["threads"]![2]!["messages"]!.AsArray();
        messages[3]!.AsObject().Remove("from");
        return node.ToJsonString();
    }

    private static async Task<Fixture> SeedAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", PhoneNumberId, "222");
        var f = new Fixture(connectionString, tenant.TenantId, connectionId);

        await InsertDeliveryAsync(f, RealShapedPayload());
        // Lo que no es historial, o no tiene la forma esperada, no estorba: el parser tampoco se cae con esto.
        await InsertDeliveryAsync(f, MetaPayloads.InboundText(PhoneNumberId, ManagedWaId, "wamid.other", Base.ToUnixTimeSeconds(), "en vivo"));
        await InsertDeliveryAsync(f, """{"entry":"x"}""");
        await InsertDeliveryAsync(f, """{"entry":[{"changes":[{"field":"history","value":{"history":"x"}},{"field":"history","value":{"history":[{"threads":[1,{"id":"573000000001","messages":3}]}]}}]}]}""");

        // Entró abierta porque su último mensaje, del negocio, se importó como del contacto.
        var managed = await InsertConversationAsync(f, ManagedWaId, "Open", 2, managed: true, lastInboundAt: null);
        await InsertMessageAsync(f, managed, "wamid.managed.in", Base, Inbound, Delivered, imported: true);
        var managedLast = await InsertMessageAsync(f, managed, "wamid.managed.out", Base.AddMinutes(1), Inbound, Delivered, imported: true);
        await SetSummaryAsync(f, managed, managedLast, Base.AddMinutes(1), Inbound, Delivered);

        // Un asesor ya la leyó (status 3) y el flujo en vivo la lleva. Un evento posterior no es la foto de la lista.
        var advisor = await InsertConversationAsync(f, AdvisorWaId, "Open", 0, managed: false, lastInboundAt: Base.AddHours(1));
        var advisorLast = await InsertMessageAsync(f, advisor, "wamid.advisor.out", Base, Inbound, Read, imported: true);
        await InsertEventAsync(f, advisor, Base.AddHours(2));
        await SetSummaryAsync(f, advisor, advisorLast, Base, Inbound, Read);

        // El último es del contacto, y en el mismo segundo que el del negocio: Meta mandó primero el del negocio.
        var settled = await InsertConversationAsync(f, SettledWaId, "Resolved", 0, managed: true, lastInboundAt: null);
        await InsertMessageAsync(f, settled, "wamid.settled.purged", Base.AddHours(-1), Inbound, Delivered, imported: true);
        await InsertMessageAsync(f, settled, "wamid.settled.nofrom", Base.AddMinutes(-2), Inbound, Delivered, imported: true);
        await InsertMessageAsync(f, settled, "wamid.settled.live", Base.AddMinutes(-1), Inbound, Delivered, imported: false);
        var settledLast = await InsertMessageAsync(f, settled, "wamid.settled.in", Base, Inbound, Delivered, imported: true);
        await InsertMessageAsync(f, settled, "wamid.settled.out", Base, Inbound, Delivered, imported: true);
        await SetSummaryAsync(f, settled, settledLast, Base, Inbound, Delivered);
        return f;
    }

    private static Task<string> MessageAsync(Fixture f, string wamid) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT direction || '|' || status || '|' || sent_by_kind FROM messaging.messages WHERE wamid = @wamid", ("wamid", wamid));

    private static Task<string> ConversationAsync(Fixture f, string waId) =>
        ScalarAsync<string>(
            f.ConnectionString,
            """
            SELECT status || '|' || unread_count || '|' || last_message_direction || '|' || last_message_status || '|' || last_message_preview
                || '|' || CASE WHEN history_managed THEN 'managed' ELSE 'live' END || '|' || version
              FROM messaging.conversations WHERE wa_id = @waId
            """,
            ("waId", waId));

    private static Task<string> SnapshotAsync(Fixture f) =>
        ScalarAsync<string>(
            f.ConnectionString,
            """
            SELECT (SELECT md5(string_agg(id::text || ':' || xmin::text, ',' ORDER BY id)) FROM messaging.messages) || '/' ||
                   (SELECT md5(string_agg(id::text || ':' || xmin::text, ',' ORDER BY id)) FROM messaging.conversations)
            """);

    [Fact]
    public async Task TheScriptFixesTheImportedMessagesAndTheSummaryOnly()
    {
        await using var database = await StartDatabaseAsync();
        var f = await SeedAsync(database);

        var results = await RunAsync(f.ConnectionString, CommittingScript());

        // Los del negocio, incluido uno que ya estaba leído.
        Assert.Equal($"{Outbound}|{Sent}|1", await MessageAsync(f, "wamid.managed.out"));
        Assert.Equal($"{Outbound}|{Sent}|1", await MessageAsync(f, "wamid.advisor.out"));
        Assert.Equal($"{Outbound}|{Sent}|1", await MessageAsync(f, "wamid.settled.out"));
        // Lo del contacto, lo que entró en vivo, lo que no trae `from` y lo que ya no está en ninguna entrega no cambian.
        Assert.Equal($"{Inbound}|{Delivered}|1", await MessageAsync(f, "wamid.managed.in"));
        Assert.Equal($"{Inbound}|{Delivered}|1", await MessageAsync(f, "wamid.settled.in"));
        Assert.Equal($"{Inbound}|{Delivered}|1", await MessageAsync(f, "wamid.settled.live"));
        Assert.Equal($"{Inbound}|{Delivered}|1", await MessageAsync(f, "wamid.settled.nofrom"));
        Assert.Equal($"{Inbound}|{Delivered}|1", await MessageAsync(f, "wamid.settled.purged"));

        // La foto cambia y la versión sube. Estado y no leídos quedan como estaban, también en la que sigue con history_managed.
        Assert.Equal($"Open|2|{Outbound}|{Sent}|vista previa|managed|2", await ConversationAsync(f, ManagedWaId));
        Assert.Equal($"Open|0|{Outbound}|{Sent}|vista previa|live|2", await ConversationAsync(f, AdvisorWaId));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.conversations WHERE wa_id = @waId AND last_inbound_at = @at", ("waId", AdvisorWaId), ("at", Base.AddHours(1))));
        // Su último mensaje sigue siendo del contacto: nada que escribir, y la versión no se mueve (un 412 gratis para el asesor).
        Assert.Equal($"Resolved|0|{Inbound}|{Delivered}|vista previa|managed|1", await ConversationAsync(f, SettledWaId));

        Assert.Equal(4L, results["payload: mensajes del negocio"]);
        Assert.Equal(3L, results["mensajes a corregir"]);
        Assert.Equal(1L, results["payload: mensajes sin from (quedan entrantes)"]);
        Assert.Equal(1L, results["payload: del negocio pero en vivo (no se tocan)"]);
        Assert.Equal(1L, results["importados entrantes sin entrega (no se pueden corregir)"]);
        Assert.Equal(2L, results["conversaciones abiertas que seguirán abiertas con el último del negocio"]);
        Assert.Equal(3L, results["mensajes corregidos"]);
        Assert.Equal(2L, results["conversaciones con foto corregida"]);
    }

    [Fact]
    public async Task ASecondRunChangesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var f = await SeedAsync(database);
        await RunAsync(f.ConnectionString, CommittingScript());
        var before = await SnapshotAsync(f);

        var results = await RunAsync(f.ConnectionString, CommittingScript());

        Assert.Equal(before, await SnapshotAsync(f));
        Assert.Equal(0L, results["mensajes a corregir"]);
        Assert.Equal(0L, results["mensajes corregidos"]);
        Assert.Equal(0L, results["conversaciones con foto corregida"]);
    }

    [Fact]
    public async Task AsWrittenTheScriptRollsBack()
    {
        await using var database = await StartDatabaseAsync();
        var f = await SeedAsync(database);
        var before = await SnapshotAsync(f);

        var results = await RunAsync(f.ConnectionString, await File.ReadAllTextAsync(ScriptPath(), TestContext.Current.CancellationToken));

        Assert.Equal(3L, results["mensajes corregidos"]);
        Assert.Equal(before, await SnapshotAsync(f));
    }
}
