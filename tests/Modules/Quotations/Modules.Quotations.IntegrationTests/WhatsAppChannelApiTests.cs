using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
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

    private static async Task<(Guid TenantId, QuotationResponse Quotation, HttpClient Client)> DraftQuotationAsync(
        QepApiFactory factory)
    {
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        return (tenantId, await CreateQuotationAsync(client, tenantId, clientId), client);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, Guid tenantId, Guid quotationId) =>
        client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/send", new SendQuotationRequest(), TestContext.Current.CancellationToken);

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
