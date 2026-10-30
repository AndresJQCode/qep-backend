using System.Globalization;
using System.Net;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Decisión del owner del 2026-10-30 (hallazgo F3 de la revisión final): el historial importado no infla el reporte de mensajería el
/// día de la conexión. Una conversación o un mensaje importados no arman sesiones, no son respuestas y no hacen nuevo a un contacto; sólo
/// sirven para saber que el contacto ya había escrito. El tráfico en vivo posterior cuenta como siempre, también sobre una conversación
/// importada. Todo pasa por el webhook real, con el reloj real: así <c>created_at</c> es la hora de la importación, como en producción.
/// </summary>
public sealed class CoexistenceReportTests
{
    private const string PhoneNumberId = "111";
    private const string EchoWaId = "573001110005";

    private const string Catalog = """
        {"data":[{"id":"1","name":"seguimiento","language":"es","status":"APPROVED","category":"MARKETING","parameter_format":"NAMED",
                  "components":[{"type":"BODY","text":"Hola {{first_name}}, ¿sigues interesado?"}]}]}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(
        QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Webhook, HttpClient Reader, long ConnectedAt)
        : IDisposable
    {
        public Guid TenantId => Tenant.TenantId;

        public void Dispose()
        {
            Webhook.Dispose();
            Reader.Dispose();
            Factory.Dispose();
        }
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", PhoneNumberId, "222");
        var connectedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Equal(1, await ExecuteAsync(
            connectionString,
            """
            INSERT INTO messaging.history_syncs
                (connection_id, tenant_id, connected_at, contacts_state, history_state, attempts, created_at, updated_at, version)
            VALUES (@connectionId, @tenantId, to_timestamp(@connectedAt), 'Requested', 'Requested', 0, now(), now(), 1)
            """,
            ("connectionId", connectionId), ("tenantId", tenant.TenantId), ("connectedAt", connectedAt)));
        var reader = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        return new Fixture(factory, connectionString, tenant, connectionId, factory.CreateClient(), reader, connectedAt);
    }

    private static async Task PostAsync(Fixture f, string json)
    {
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(f.Webhook, json)).StatusCode);
        await DrainDeliveriesAsync(f.Factory);
    }

    private static long HoursBefore(Fixture f, int hours) => f.ConnectedAt - (hours * 3600L);

    /// <summary>Ayer y hoy en Bogotá (la zona del tenant): el entrante importado de hace una hora cae dentro sin importar la hora a la
    /// que corra la prueba.</summary>
    private static async Task<JsonElement> ReportAsync(Fixture f)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-5)).DateTime);
        var from = today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var response = await f.Reader.GetAsync($"/api/v1/tenants/{f.TenantId}/messaging/reports/summary?from={from}&to={to}&groupBy=day", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string[] Channels(JsonElement report) =>
        [.. report.GetProperty("channelsWithData").EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    public async Task ImportedHistoryAloneDoesNotCountInTheReport()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        // Una abierta (el contacto escribió hace una hora) y una cerrada (respondida hace un mes).
        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 0, 1, 100,
            ("573001110001", [("wamid.a1", "¿Tienen envío?", HoursBefore(f, 1), false)]),
            ("573001110002", [("wamid.b1", "Hola", HoursBefore(f, 720), false), ("wamid.b2", "Con gusto", HoursBefore(f, 719), true)])));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.conversations"));

        var report = await ReportAsync(f);

        var current = report.GetProperty("totals").GetProperty("current");
        Assert.Equal(0, current.GetProperty("conversations").GetInt32());
        Assert.Equal(0, current.GetProperty("newContacts").GetInt32());
        Assert.Equal(0, current.GetProperty("returningContacts").GetInt32());
        Assert.Equal(0, report.GetProperty("firstResponse").GetProperty("unansweredSessions").GetInt32());
        Assert.Empty(Channels(report));
    }

    [Fact]
    public async Task LiveTrafficAfterTheConnectionCountsAlsoOnImportedConversations()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 0, 1, 50,
            ("573001110001", [("wamid.a1", "¿Tienen envío?", HoursBefore(f, 1), false)]),
            ("573001110002", [("wamid.b1", "Hola", HoursBefore(f, 720), false), ("wamid.b2", "Con gusto", HoursBefore(f, 719), true)]),
            ("573001110003", [("wamid.c1", "Gracias", HoursBefore(f, 2), false)])));

        // A, importada abierta, recibe un entrante en vivo (no se reabre: ya estaba abierta). B, importada cerrada, se reabre. D es nueva.
        await PostAsync(f, MetaPayloads.Inbound(PhoneNumberId, "CO.A", "573001110001", "wamid.a-live", f.ConnectedAt + 60, "¿Me respondes?"));
        await PostAsync(f, MetaPayloads.Inbound(PhoneNumberId, "CO.B", "573001110002", "wamid.b-live", f.ConnectedAt + 120, "Volví"));
        await PostAsync(f, MetaPayloads.Inbound(PhoneNumberId, "CO.D", "573001110004", "wamid.d-live", f.ConnectedAt + 180, "Primera vez"));

        // Una parte posterior del historial trae un saliente viejo de B: llega con created_at después del entrante en vivo, y no es una
        // respuesta.
        await PostAsync(f, MetaPayloads.History(PhoneNumberId, 1, 1, 100,
            ("573001110002", [("wamid.b0", "Mensaje viejo", HoursBefore(f, 1000), true)])));

        var report = await ReportAsync(f);

        var current = report.GetProperty("totals").GetProperty("current");
        // A (su primer entrante en vivo), B (el Reopened) y D (creada en vivo). C sólo tiene historial.
        Assert.Equal(3, current.GetProperty("conversations").GetInt32());
        // D, y A: su primer entrante, el importado de hace una hora, cae en el periodo. B escribió hace un mes: vuelve.
        Assert.Equal(2, current.GetProperty("newContacts").GetInt32());
        Assert.Equal(1, current.GetProperty("returningContacts").GetInt32());
        var first = report.GetProperty("firstResponse");
        Assert.Equal(0, first.GetProperty("answeredSessions").GetInt32());
        Assert.Equal(3, first.GetProperty("unansweredSessions").GetInt32());
        Assert.Equal(["WhatsApp"], Channels(report));
    }

    // Revisión de los ajustes: una conversación que nació de un eco nace cerrada, así que el contacto la reabre. La creación no arranca
    // sesión: si arrancara, la misma conversación contaría dos veces.
    [Fact]
    public async Task AnEchoThenALiveInboundInThePeriodIsOneSession()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await PostAsync(f, MetaPayloads.SmbEchoes(PhoneNumberId, EchoWaId, "wamid.echo", "Te escribo desde el celular", f.ConnectedAt + 60));
        await PostAsync(f, MetaPayloads.Inbound(PhoneNumberId, "CO.E", EchoWaId, "wamid.live", f.ConnectedAt + 120, "Hola"));
        await PostAsync(f, MetaPayloads.SmbEchoes(PhoneNumberId, EchoWaId, "wamid.echo2", "Claro que sí", f.ConnectedAt + 180));

        var report = await ReportAsync(f);

        var current = report.GetProperty("totals").GetProperty("current");
        Assert.Equal(1, current.GetProperty("conversations").GetInt32());
        Assert.Equal(1, current.GetProperty("newContacts").GetInt32());
        // El eco posterior al entrante sigue siendo una respuesta: es tráfico en vivo.
        Assert.Equal(1, report.GetProperty("firstResponse").GetProperty("answeredSessions").GetInt32());
        Assert.Equal(0, report.GetProperty("firstResponse").GetProperty("unansweredSessions").GetInt32());
        Assert.Equal(["WhatsApp"], Channels(report));
    }

    [Fact]
    public async Task APeriodWithOnlyTheEchoHasNoSessions()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await PostAsync(f, MetaPayloads.SmbEchoes(PhoneNumberId, EchoWaId, "wamid.echo", "Te escribo desde el celular", f.ConnectedAt + 60));

        var report = await ReportAsync(f);

        Assert.Equal(0, report.GetProperty("totals").GetProperty("current").GetProperty("conversations").GetInt32());
        Assert.Empty(Channels(report));
    }

    // Guarda: la conversación que abre un asesor con una plantilla sí cuenta desde su creación, aunque no tenga entrante (caso D del
    // reporte, S3). Un filtro por «nació con entrante» la perdería.
    [Fact]
    public async Task AConversationAnAdvisorStartsCountsFromItsCreation()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        f.Factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        f.Factory.MetaHandler.RespondWith("/111/messages", _ => FakeMetaGraphHandler.Json(
            HttpStatusCode.OK, """{"messaging_product":"whatsapp","messages":[{"id":"wamid.start"}]}"""));
        var customerId = await CreateCustomerAsync(f.Factory, f.Tenant, "Droguería Central", "300 123 4567");
        using var advisor = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.TenantId, ManagePermissions);

        var started = await SendAsync(advisor, HttpMethod.Post, ConversationsUrl(f.TenantId), new
        {
            clientId = Guid.CreateVersion7(),
            connectionId = f.ConnectionId,
            customerId,
            template = new { name = "seguimiento", language = "es", parameters = new Dictionary<string, string> { ["first_name"] = "Ana" } },
        });
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);

        var report = await ReportAsync(f);
        Assert.Equal(1, report.GetProperty("totals").GetProperty("current").GetProperty("conversations").GetInt32());
        Assert.Equal(["WhatsApp"], Channels(report));

        // El contacto contesta: la conversación estaba abierta, así que sigue siendo una sola sesión.
        await PostAsync(f, MetaPayloads.InboundText(PhoneNumberId, "573001234567", "wamid.reply", f.ConnectedAt + 60, "Sí, cuéntame"));

        var afterReply = await ReportAsync(f);
        Assert.Equal(1, afterReply.GetProperty("totals").GetProperty("current").GetProperty("conversations").GetInt32());
    }
}
