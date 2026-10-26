using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Plan 2026-10-21 (Zenvia por tenant): la cotización sale por la conexión de Zenvia del tenant. Sin canal, o con varios
/// mientras no exista la elección (plan 3), el envío responde 422 sin tocar la cotización ni anotar una falla.
/// </summary>
public sealed class WhatsAppChannelApiTests
{
    [Fact]
    public async Task SendUsesTheTenantChannel()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, quotation, client) = await DraftQuotationAsync(factory);
        using var _ = client;

        using var response = await SendAsync(client, tenantId, quotation.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(factory.WhatsAppSender.Sent);
        Assert.Equal(RecordingWhatsAppChannelDirectory.Default.ConnectionId, sent.Channel.ConnectionId);
    }

    [Fact]
    public async Task SendWithoutChannelResponds422AndKeepsDraft()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, quotation, client) = await DraftQuotationAsync(factory);
        using var _ = client;
        factory.WhatsAppChannels.Channels.Clear();

        using var response = await SendAsync(client, tenantId, quotation.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("quotation.whatsapp.no_channel", await CodeAsync(response));
        Assert.Equal("Draft", (await GetAsync(client, tenantId, quotation.Id)).Status);
        Assert.DoesNotContain(await HistoryEventsAsync(client, tenantId, quotation.Id), type => type == "SendFailed");
        Assert.Empty(factory.WhatsAppSender.Sent);
    }

    [Fact]
    public async Task SendWithSeveralChannelsResponds422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, quotation, client) = await DraftQuotationAsync(factory);
        using var _ = client;
        factory.WhatsAppChannels.Channels.Add(RecordingWhatsAppChannelDirectory.Default with { ConnectionId = Guid.CreateVersion7() });

        using var response = await SendAsync(client, tenantId, quotation.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("quotation.whatsapp.channel_required", await CodeAsync(response));
        Assert.Equal("Draft", (await GetAsync(client, tenantId, quotation.Id)).Status);
        Assert.Empty(factory.WhatsAppSender.Sent);
    }

    // Review Focus 3: el tenant se quedó sin canal después del primer envío.
    [Fact]
    public async Task ResendWithoutChannelKeepsSentAndRecordsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, quotation, client) = await DraftQuotationAsync(factory);
        using var _ = client;
        using (var first = await SendAsync(client, tenantId, quotation.Id))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        factory.WhatsAppChannels.Channels.Clear();
        using var response = await SendAsync(client, tenantId, quotation.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("quotation.whatsapp.no_channel", await CodeAsync(response));
        Assert.Equal("Sent", (await GetAsync(client, tenantId, quotation.Id)).Status);
        var events = await HistoryEventsAsync(client, tenantId, quotation.Id);
        Assert.DoesNotContain(events, type => type == "SendFailed");
        Assert.DoesNotContain(events, type => type == "Resent");
        Assert.Single(factory.WhatsAppSender.Sent);
    }

    // Criterio 2: la primera elección se guarda junto con el envío, es del tenant y no de quien la hizo.
    [Fact]
    public async Task TheFirstChoiceIsSavedWithTheSendAndTheNextSendDoesNotAsk()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var clientId = await CreateActiveCustomerAsync(owner, tenantId);
        var first = await CreateQuotationAsync(owner, tenantId, clientId);
        var second = await CreateQuotationAsync(owner, tenantId, clientId);
        factory.WhatsAppChannels.Channels.Add(RecordingWhatsAppChannelDirectory.Meta);

        using var asking = await SendAsync(owner, tenantId, first.Id);
        using var chosen = await SendAsync(owner, tenantId, first.Id, RecordingWhatsAppChannelDirectory.Meta.ConnectionId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, asking.StatusCode);
        Assert.Equal("quotation.whatsapp.channel_required", await CodeAsync(asking));
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        Assert.Equal(
            RecordingWhatsAppChannelDirectory.Meta.ConnectionId, Assert.Single(factory.WhatsAppSender.Sent).Channel.ConnectionId);

        using var colleague = await AddMemberClientAsync(factory, database, tenantId, ownerUserId);
        using var next = await SendAsync(colleague, tenantId, second.Id);

        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(2, factory.WhatsAppSender.Sent.Count);
        Assert.Equal(
            RecordingWhatsAppChannelDirectory.Meta.ConnectionId, factory.WhatsAppSender.Sent[1].Channel.ConnectionId);
    }

    [Fact]
    public async Task AConnectionIdWithAValidChoiceIs422AlreadySet()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var clientId = await CreateActiveCustomerAsync(owner, tenantId);
        var first = await CreateQuotationAsync(owner, tenantId, clientId);
        var second = await CreateQuotationAsync(owner, tenantId, clientId);
        factory.WhatsAppChannels.Channels.Add(RecordingWhatsAppChannelDirectory.Meta);
        using (var chosen = await SendAsync(owner, tenantId, first.Id, RecordingWhatsAppChannelDirectory.Meta.ConnectionId))
        {
            Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        }

        using var response = await SendAsync(owner, tenantId, second.Id, RecordingWhatsAppChannelDirectory.Default.ConnectionId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("quotation.whatsapp.channel_already_set", await CodeAsync(response));
        Assert.Equal("Draft", (await GetAsync(owner, tenantId, second.Id)).Status);
        Assert.DoesNotContain(await HistoryEventsAsync(owner, tenantId, second.Id), type => type == "SendFailed");
    }

    [Fact]
    public async Task AnUnavailableConnectionIdIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, quotation, client) = await DraftQuotationAsync(factory);
        using var _ = client;

        using var response = await SendAsync(client, tenantId, quotation.Id, Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("quotation.whatsapp.channel_unavailable", await CodeAsync(response));
        Assert.Equal("Draft", (await GetAsync(client, tenantId, quotation.Id)).Status);
        Assert.Empty(factory.WhatsAppSender.Sent);
    }

    // Review Focus 1: la elección guardada se perdió y quedan varios: se pregunta de nuevo y la nueva reemplaza la vencida.
    [Fact]
    public async Task AStaleChoiceWithSeveralOthersAsksAgainAndTheNewChoiceReplacesIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var clientId = await CreateActiveCustomerAsync(owner, tenantId);
        var quotations = new List<QuotationResponse>();
        for (var index = 0; index < 3; index++)
        {
            quotations.Add(await CreateQuotationAsync(owner, tenantId, clientId));
        }

        var a = RecordingWhatsAppChannelDirectory.Default;
        var b = RecordingWhatsAppChannelDirectory.Meta;
        var c = RecordingWhatsAppChannelDirectory.Default with { ConnectionId = Guid.CreateVersion7() };
        factory.WhatsAppChannels.Channels.Add(b);
        using (var chosen = await SendAsync(owner, tenantId, quotations[0].Id, a.ConnectionId))
        {
            Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        }

        factory.WhatsAppChannels.Channels.Clear();
        factory.WhatsAppChannels.Channels.AddRange([b, c]);
        using var asking = await SendAsync(owner, tenantId, quotations[1].Id);
        using var rechosen = await SendAsync(owner, tenantId, quotations[1].Id, b.ConnectionId);
        using var next = await SendAsync(owner, tenantId, quotations[2].Id);

        Assert.Equal("quotation.whatsapp.channel_required", await CodeAsync(asking));
        Assert.Equal(HttpStatusCode.OK, rechosen.StatusCode);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(
            [a.ConnectionId, b.ConnectionId, b.ConnectionId],
            factory.WhatsAppSender.Sent.Select(sent => sent.Channel.ConnectionId));
    }

    // Review Focus 1: con una sola conexión disponible se usa sin escribir, así que la elección guardada sigue ahí.
    [Fact]
    public async Task AStaleChoiceWithOneOtherUsesItWithoutSaving()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var clientId = await CreateActiveCustomerAsync(owner, tenantId);
        var quotations = new List<QuotationResponse>();
        for (var index = 0; index < 3; index++)
        {
            quotations.Add(await CreateQuotationAsync(owner, tenantId, clientId));
        }

        var a = RecordingWhatsAppChannelDirectory.Default;
        var b = RecordingWhatsAppChannelDirectory.Meta;
        factory.WhatsAppChannels.Channels.Add(b);
        using (var chosen = await SendAsync(owner, tenantId, quotations[0].Id, a.ConnectionId))
        {
            Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        }

        factory.WhatsAppChannels.Channels.Clear();
        factory.WhatsAppChannels.Channels.Add(b);
        using var onlyB = await SendAsync(owner, tenantId, quotations[1].Id);
        factory.WhatsAppChannels.Channels.Insert(0, a);
        using var backToA = await SendAsync(owner, tenantId, quotations[2].Id);

        Assert.Equal(HttpStatusCode.OK, onlyB.StatusCode);
        Assert.Equal(HttpStatusCode.OK, backToA.StatusCode);
        Assert.Equal(
            [a.ConnectionId, b.ConnectionId, a.ConnectionId],
            factory.WhatsAppSender.Sent.Select(sent => sent.Channel.ConnectionId));
    }

    // Review Focus 5: la elección viaja con el `Sent`; un envío que falló no la guarda.
    [Fact]
    public async Task AFailedSendDoesNotRememberTheChoice()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, quotation, client) = await DraftQuotationAsync(factory);
        using var _ = client;
        factory.WhatsAppChannels.Channels.Add(RecordingWhatsAppChannelDirectory.Meta);
        factory.WhatsAppSender.Failure = new InvalidOperationException("Meta no respondió.");

        using var failed = await SendAsync(client, tenantId, quotation.Id, RecordingWhatsAppChannelDirectory.Meta.ConnectionId);
        factory.WhatsAppSender.Failure = null;
        using var asking = await SendAsync(client, tenantId, quotation.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal("quotation.send.failed", await CodeAsync(failed));
        Assert.Equal("quotation.whatsapp.channel_required", await CodeAsync(asking));
    }

    private static async Task<(Guid TenantId, QuotationResponse Quotation, HttpClient Client)> DraftQuotationAsync(
        QepApiFactory factory)
    {
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        return (tenantId, await CreateQuotationAsync(client, tenantId, clientId), client);
    }

    // Otro miembro activo del mismo tenant: se invita con el set por defecto del stub (que trae los permisos de Tenancy)
    // y se activa por SQL, que se salta el login de Google que pide aceptar la invitación.
    private static async Task<HttpClient> AddMemberClientAsync(
        QepApiFactory factory, PostgreSqlContainer database, Guid tenantId, Guid ownerUserId)
    {
        using var admin = CreateClient(factory, ownerUserId.ToString(), tenantId.ToString());
        using var invited = await admin.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/memberships",
            new
            {
                email = $"member-{Guid.NewGuid():N}@example.com",
                displayName = "Ana Pérez",
                roles = AdvisorRoles,
                advisorCode = (int?)null,
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
        var member = await invited.Content.ReadFromJsonAsync<InvitedMemberPayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(member);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE tenancy.memberships SET state = 'Active', accepted_at = now() WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", member.Id);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        return CreateClient(factory, member.UserId.ToString(), tenantId.ToString(), ManagerPermissions);
    }

    private static readonly string[] AdvisorRoles = ["advisor"];

    private sealed record InvitedMemberPayload(Guid Id, Guid UserId);

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, Guid tenantId, Guid quotationId, Guid? connectionId = null) =>
        client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/send",
            new SendQuotationRequest(ConnectionId: connectionId),
            TestContext.Current.CancellationToken);

    private static async Task<QuotationResponse> GetAsync(HttpClient client, Guid tenantId, Guid quotationId) =>
        (await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotationId}", TestContext.Current.CancellationToken))!;

    private static async Task<IReadOnlyList<string>> HistoryEventsAsync(HttpClient client, Guid tenantId, Guid quotationId)
    {
        var history = await client.GetFromJsonAsync<QuotationHistoryResponse>(
            $"{QuotationsUrl(tenantId)}/{quotationId}/history", TestContext.Current.CancellationToken);
        return [.. history!.Items.Select(entry => entry.EventType)];
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.GetProperty("code").GetString();
    }
}
