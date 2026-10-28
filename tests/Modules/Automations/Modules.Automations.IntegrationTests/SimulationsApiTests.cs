using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Automations.Api;
using Modules.Automations.Application;
using Modules.Automations.Domain;
using Modules.Messaging.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Spec 2026-10-21 §4.4, §8 y D-S1..D-S3 contra Postgres: el simulador del editor corre el borrador con el motor real, guarda su estado en
/// <c>automations.simulations</c> (vence a las 2 h, el barrido la borra) y no toca Mensajería. Las llamadas HTTP salen sólo con
/// <c>liveHttp</c> y quedan en <c>http_calls</c> con la simulación y sin corrida. Todo con <c>automations.chatbot.manage</c>.
/// </summary>
public sealed class SimulationsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Botones (tech / maint) → Pedir adjunto → mensaje con el nombre del archivo → resolver.</summary>
    private const string ButtonsThenAttachmentFlow = """
        { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
          "nodes": [
            { "id": "n1", "kind": "buttons", "title": "¿Qué necesitas?", "text": "¿Qué necesitas para tu equipo?",
              "buttons": [ { "id": "tech", "label": "Servicio técnico" }, { "id": "maint", "label": "Mantenimiento" } ], "saveTo": "motivo",
              "fallback": "repeat", "outputs": { "tech": "n2", "maint": "n4", "other": null } },
            { "id": "n2", "kind": "attachment", "title": "Foto de la factura", "text": "Envíanos la factura.", "accept": "any", "saveTo": "factura",
              "outputs": { "received": "n3", "invalid": null } },
            { "id": "n3", "kind": "message", "title": "Recibida", "text": "Recibimos {factura.fileName}.", "outputs": { "next": "n5" } },
            { "id": "n4", "kind": "handoff", "title": "Asesor", "text": "Un asesor te escribe." },
            { "id": "n5", "kind": "resolve", "title": "Listo" }
          ] }
        """;

    private const string BusinessHoursFlow = """
        { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
          "nodes": [
            { "id": "n1", "kind": "businessHours", "title": "Horario", "outputs": { "open": "n2", "closed": "n3" } },
            { "id": "n2", "kind": "message", "title": "Abiertos", "text": "Estamos atendiendo.", "outputs": { "next": "n4" } },
            { "id": "n3", "kind": "message", "title": "Cerrados", "text": "Estamos cerrados.", "outputs": { "next": "n4" } },
            { "id": "n4", "kind": "resolve", "title": "Listo" }
          ] }
        """;

    private const string ConsentFlow = """
        { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
          "nodes": [
            { "id": "n1", "kind": "dataConsent", "title": "Política de datos", "text": "Lee https://e.co/d",
              "outputs": { "accepted": "n2", "rejected": "n3" } },
            { "id": "n2", "kind": "message", "title": "Gracias", "text": "Gracias", "outputs": { "next": "n4" } },
            { "id": "n3", "kind": "message", "title": "Entendido", "text": "Entendido", "outputs": { "next": "n4" } },
            { "id": "n4", "kind": "resolve", "title": "Listo" }
          ] }
        """;

    private static string LiveHttpFlow(Guid connectionId) => $$"""
        { "schema": 1, "start": { "firstNodeId": "n1" }, "variables": [],
          "nodes": [
            { "id": "n1", "kind": "http", "title": "Buscar cliente", "connectionId": "{{connectionId}}", "method": "GET", "path": "/customers",
              "mapping": [ { "from": "data[0]", "to": "cliente" } ], "sampleResponse": { "data": [ { "id": 1 } ] },
              "outputs": { "ok": "n2", "error": "n3" } },
            { "id": "n2", "kind": "message", "title": "Encontrado", "text": "Cliente {cliente.id}", "outputs": { "next": "n4" } },
            { "id": "n3", "kind": "message", "title": "Falló", "text": "No pudimos buscarte.", "outputs": { "next": "n4" } },
            { "id": "n4", "kind": "resolve", "title": "Listo" }
          ] }
        """;

    private sealed record Setup(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client) : IDisposable
    {
        public string Url => AutomationsUrl(Tenant.TenantId);

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private static async Task<Setup> SetupAsync(TestDatabase database, FakeExternalApis? externalApis = null)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString, externalApis);
        var tenant = await RegisterTenantAsync(factory);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        return new Setup(factory, connectionString, tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions));
    }

    /// <summary>Un chatbot en borrador con <paramref name="flowJson"/> (sin él, el borrador en blanco: tiene errores).</summary>
    private static async Task<Guid> CreateDraftAsync(Setup setup, string? flowJson, params Guid[] connectionIds)
    {
        var created = await setup.Client.PostAsJsonAsync(
            $"{setup.Url}/chatbots",
            new CreateChatbotRequest("Servicio", string.Empty, "NewConversation", [], connectionIds, new ChatbotSourceRequest("blank", null, null, null)),
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var editor = (await created.Content.ReadFromJsonAsync<ChatbotEditorDto>(Ct))!;
        if (flowJson is not null)
        {
            using var draft = new HttpRequestMessage(HttpMethod.Put, $"{setup.Url}/chatbots/{editor.Chatbot.Id}/draft")
            {
                Content = JsonContent.Create(new SaveDraftRequest(JsonDocument.Parse(flowJson).RootElement, JsonDocument.Parse("{}").RootElement)),
            };
            draft.Headers.TryAddWithoutValidation("If-Match", $"\"{editor.Chatbot.Version}\"");
            Assert.Equal(HttpStatusCode.OK, (await setup.Client.SendAsync(draft, Ct)).StatusCode);
        }

        return editor.Chatbot.Id;
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, Setup setup, Guid chatbotId, bool liveHttp = false) =>
        client.PostAsJsonAsync($"{setup.Url}/chatbots/{chatbotId}/simulations", new StartSimulationRequest(liveHttp), Ct);

    private static async Task<SimulationDto> StartOkAsync(Setup setup, Guid chatbotId, bool liveHttp = false)
    {
        var response = await StartAsync(setup.Client, setup, chatbotId, liveHttp);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SimulationDto>(Ct))!;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, Setup setup, Guid simulationId, SimulationMessageRequest message) =>
        client.PostAsJsonAsync($"{setup.Url}/simulations/{simulationId}/messages", message, Ct);

    private static async Task<SimulationDto> SendOkAsync(Setup setup, Guid simulationId, SimulationMessageRequest message)
    {
        var response = await SendAsync(setup.Client, setup, simulationId, message);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SimulationDto>(Ct))!;
    }

    [Fact]
    public async Task StartReturnsTheInitialTranscriptAndCurrentNode()
    {
        await using var database = await StartDatabaseAsync();
        using var setup = await SetupAsync(database, FakeExternalApis.WithAnnexA());
        var chatbotId = await CreateDraftAsync(setup, AnnexA.Json);

        var simulation = await StartOkAsync(setup, chatbotId);

        Assert.Equal(chatbotId, simulation.ChatbotId);
        Assert.False(simulation.LiveHttp);
        Assert.Equal(["bot", "bot"], simulation.Transcript.Select(entry => entry.From));
        Assert.Equal(["Bienvenida", "Número de documento"], simulation.Transcript.Select(entry => entry.NodeTitle));
        Assert.Equal("n2", simulation.CurrentNodeId);
        Assert.Equal(("Waiting", false), (simulation.Status, simulation.Ended));
        Assert.Equal("Cliente de prueba", simulation.Variables.GetProperty("nombre").GetString());
        Assert.Equal("3000000000", simulation.Variables.GetProperty("telefono").GetString());
        Assert.Equal("Automations Test Org", simulation.Variables.GetProperty("empresa").GetString());
        // Sin conexiones en el chatbot, «Simulador».
        Assert.Equal("Simulador", simulation.Variables.GetProperty("conexion").GetString());
        Assert.InRange(simulation.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(119), DateTimeOffset.UtcNow.AddMinutes(121));
        // D-S1: ni corridas ni nada en Mensajería; el estado vive en automations.simulations.
        Assert.Equal(1L, await CountAsync(setup.ConnectionString, "SELECT count(*) FROM automations.simulations WHERE id = @id", ("id", simulation.Id)));
        Assert.Equal(0L, await CountAsync(setup.ConnectionString, "SELECT count(*) FROM automations.conversation_runs"));
        Assert.Equal(0L, await CountAsync(setup.ConnectionString, "SELECT count(*) FROM messaging.messages"));
    }

    [Fact]
    public async Task StartOnADraftWithErrorsIs422Invalid()
    {
        await using var database = await StartDatabaseAsync();
        using var setup = await SetupAsync(database);
        var chatbotId = await CreateDraftAsync(setup, flowJson: null);

        var response = await StartAsync(setup.Client, setup, chatbotId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, errorKeys) = await ProblemAsync(response);
        Assert.Equal(AutomationsErrorCodes.ChatbotInvalid, code);
        Assert.Equal(["flow"], errorKeys);
        Assert.Equal(0L, await CountAsync(setup.ConnectionString, "SELECT count(*) FROM automations.simulations"));
    }

    [Fact]
    public async Task AReplyIdAdvancesAndAnAttachmentIsAccepted()
    {
        await using var database = await StartDatabaseAsync();
        using var setup = await SetupAsync(database);
        var chatbotId = await CreateDraftAsync(setup, ButtonsThenAttachmentFlow);
        var started = await StartOkAsync(setup, chatbotId);
        var offered = Assert.Single(started.Transcript);
        Assert.Equal(
            [new TranscriptButton("tech", "Servicio técnico"), new TranscriptButton("maint", "Mantenimiento")], offered.Buttons!);

        var tapped = await SendOkAsync(setup, started.Id, new SimulationMessageRequest(null, "tech", null));
        var attached = await SendOkAsync(
            setup, started.Id, new SimulationMessageRequest(null, null, new SimulationAttachmentRequest("f.pdf", "application/pdf")));

        // La línea del tester muestra el título del botón que tocó, no su id.
        Assert.Equal(["bot", "customer", "bot"], tapped.Transcript.Select(entry => entry.From));
        Assert.Equal("Servicio técnico", tapped.Transcript[1].Text);
        Assert.Equal("n2", tapped.CurrentNodeId);
        Assert.Equal("tech", tapped.Variables.GetProperty("motivo").GetProperty("id").GetString());

        Assert.Equal(["bot", "customer", "bot", "customer", "bot", "system"], attached.Transcript.Select(entry => entry.From));
        Assert.Equal("f.pdf", attached.Transcript[3].Text);
        Assert.Equal("Recibimos f.pdf.", attached.Transcript[4].Text);
        // D-S3: resolver sólo escribe la línea; la conversación no existe.
        Assert.Equal("Resolvería la conversación", attached.Transcript[5].Text);
        var factura = attached.Variables.GetProperty("factura");
        Assert.Equal(("f.pdf", "application/pdf", false), (factura.GetProperty("fileName").GetString(), factura.GetProperty("mimeType").GetString(), factura.GetProperty("stored").GetBoolean()));
        Assert.Equal(("Resolved", true), (attached.Status, attached.Ended));
    }

    [Fact]
    public async Task BusinessHoursReadsTheRealPort()
    {
        await using var database = await StartDatabaseAsync();
        using var setup = await SetupAsync(database);
        var connectionId = await SeedWhatsAppConnectionAsync(setup.Factory, setup.Tenant.TenantId, "Ventas", "111", "222");
        // Un horario sin franjas ningún día: la conexión está siempre cerrada.
        using (var messaging = CreateClient(
            setup.Factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, MessagingPermissions.ConversationRead, MessagingPermissions.BusinessHoursManage))
        {
            var schedule = await messaging.PostAsJsonAsync(
                $"/api/v1/tenants/{setup.Tenant.TenantId}/messaging/business-hours",
                new
                {
                    name = "Cerrado",
                    weekly = Enumerable.Range(1, 7).Select(day => new { day, ranges = Array.Empty<object>() }),
                    exceptions = Array.Empty<object>(),
                    holidayCountry = (string?)null,
                    connectionIds = new[] { connectionId },
                },
                Ct);
            Assert.Equal(HttpStatusCode.Created, schedule.StatusCode);
        }

        var chatbotId = await CreateDraftAsync(setup, BusinessHoursFlow, connectionId);

        var simulation = await StartOkAsync(setup, chatbotId);

        Assert.Equal(["Estamos cerrados.", "Resolvería la conversación"], simulation.Transcript.Select(entry => entry.Text));
        Assert.Equal("Ventas", simulation.Variables.GetProperty("conexion").GetString());
    }

    /// <summary>Spec 2026-10-26 D-11: el simulador pregunta siempre y no escribe en <c>customers.data_consents</c>.</summary>
    [Fact]
    public async Task SimulatingTheConsentBlockNeverWritesRows()
    {
        await using var database = await StartDatabaseAsync();
        using var setup = await SetupAsync(database);
        var chatbotId = await CreateDraftAsync(setup, ConsentFlow);

        var started = await StartOkAsync(setup, chatbotId);
        var accepted = await SendOkAsync(setup, started.Id, new SimulationMessageRequest(null, "accepted", null));

        Assert.Equal(
            [new TranscriptButton("accepted", "Acepto"), new TranscriptButton("rejected", "No acepto")], Assert.Single(started.Transcript).Buttons!);
        Assert.Contains(accepted.Transcript, entry => entry is { From: "system", Text: "Registraría que el cliente aceptó la política de datos." });
        Assert.Equal(0L, await CountAsync(setup.ConnectionString, "SELECT count(*) FROM customers.data_consents"));
    }

    [Fact]
    public async Task LiveHttpRecordsInHttpCallsWithTheSimulationId()
    {
        await using var database = await StartDatabaseAsync();
        using var setup = await SetupAsync(database);
        var api = await SeedExternalApiConnectionAsync(setup.Factory, setup.Tenant.TenantId, setup.Tenant.OwnerUserId, "Clientes", "Active");
        setup.Factory.ExternalApi.Respond("^/customers", HttpStatusCode.OK, """{ "data": [ { "id": 7 } ] }""");
        var chatbotId = await CreateDraftAsync(setup, LiveHttpFlow(api));

        var sample = await StartOkAsync(setup, chatbotId);
        var live = await StartOkAsync(setup, chatbotId, liveHttp: true);

        // Sin liveHttp la respuesta de ejemplo (id 1) y ninguna llamada; con él, la API (id 7) y su fila.
        Assert.Equal("Cliente 1", sample.Transcript[0].Text);
        Assert.Equal("Cliente 7", live.Transcript[0].Text);
        Assert.True(live.LiveHttp);
        Assert.Single(setup.Factory.ExternalApi.Requests);
        Assert.Equal(1L, await CountAsync(setup.ConnectionString, "SELECT count(*) FROM automations.http_calls"));
        Assert.Equal(
            1L,
            await CountAsync(
                setup.ConnectionString,
                "SELECT count(*) FROM automations.http_calls WHERE simulation_id = @id AND run_id IS NULL AND node_id = 'n1' AND outcome = 'ok'",
                ("id", live.Id)));
    }

    [Fact]
    public async Task AnExpiredSimulationIs404ExpiredAndTheSweepDeletesIt()
    {
        await using var database = await StartDatabaseAsync();
        using var setup = await SetupAsync(database);
        var chatbotId = await CreateDraftAsync(setup, ButtonsThenAttachmentFlow);
        var expiring = await StartOkAsync(setup, chatbotId);
        var alive = await StartOkAsync(setup, chatbotId);
        await ExecuteAsync(
            setup.ConnectionString, "UPDATE automations.simulations SET expires_at = now() - interval '1 minute' WHERE id = @id", ("id", expiring.Id));

        var expired = await SendAsync(setup.Client, setup, expiring.Id, new SimulationMessageRequest(null, "tech", null));
        await SweepSimulationsAsync(setup.Factory, DateTimeOffset.UtcNow);
        var swept = await SendAsync(setup.Client, setup, expiring.Id, new SimulationMessageRequest(null, "tech", null));

        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Equal(AutomationsErrorCodes.SimulationExpired, (await ProblemAsync(expired)).Code);
        Assert.Equal(HttpStatusCode.NotFound, swept.StatusCode);
        Assert.Equal(AutomationsErrorCodes.SimulationNotFound, (await ProblemAsync(swept)).Code);
        Assert.Equal(
            [alive.Id],
            [await ScalarAsync<Guid>(setup.ConnectionString, "SELECT id FROM automations.simulations")]);
    }

    [Fact]
    public async Task DeleteIs204AndReadIsManageOnly()
    {
        await using var database = await StartDatabaseAsync();
        using var setup = await SetupAsync(database);
        var chatbotId = await CreateDraftAsync(setup, ButtonsThenAttachmentFlow);
        using var reader = CreateClient(setup.Factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ReadPermissions);
        var simulation = await StartOkAsync(setup, chatbotId);

        var readerStart = await StartAsync(reader, setup, chatbotId);
        var readerSend = await SendAsync(reader, setup, simulation.Id, new SimulationMessageRequest(null, "tech", null));
        var readerDelete = await reader.DeleteAsync($"{setup.Url}/simulations/{simulation.Id}", Ct);
        var deleted = await setup.Client.DeleteAsync($"{setup.Url}/simulations/{simulation.Id}", Ct);
        var again = await setup.Client.DeleteAsync($"{setup.Url}/simulations/{simulation.Id}", Ct);

        Assert.Equal(
            [HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden],
            [readerStart.StatusCode, readerSend.StatusCode, readerDelete.StatusCode]);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(AutomationsErrorCodes.SimulationNotFound, (await ProblemAsync(again)).Code);
        Assert.Equal(0L, await CountAsync(setup.ConnectionString, "SELECT count(*) FROM automations.simulations"));
    }
}
