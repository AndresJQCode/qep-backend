using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;
using static Modules.Quotations.IntegrationTests.WhatsAppTestHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Spec 2026-10-07, «Pruebas → Integración», envío: Disabled marca enviada y lo dice sin llamar a
/// nadie (también en reenvío), Own manda con el token, el número y la plantilla del tenant, y Shared
/// —explícito o volviendo de Disabled— usa el sender global. Las pruebas de envío existentes, sin
/// fila, quedan como están (criterio 1).
/// </summary>
public sealed class WhatsAppSendChannelApiTests
{
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

    [Fact]
    public async Task WithWhatsAppDisabledTheQuotationIsSentAndTheResponseSaysNoWhatsAppLeft()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var sender = new RecordingIntegrationWhatsAppSender();
        using var host = factory.WithWhatsAppSender(sender);
        var (tenantId, ownerId, bootstrap) = await RegisterTenantAsync(factory, SendPermissions);
        using var _ = bootstrap;
        using var client = CreateClientFor(host, ownerId, tenantId, SendPermissions);
        (await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"1\"")).EnsureSuccessStatusCode();
        var quotationId = await CreateSendableQuotationAsync(client, tenantId);

        // Billing sin datos propios fallaría con WhatsApp; con Disabled el destinatario se ignora.
        var response = await SendAsync(client, tenantId, quotationId, new { recipient = "Billing" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("Sent", body.GetProperty("status").GetString());
        Assert.Equal("Disabled", body.GetProperty("whatsAppOutcome").GetString());
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task ResendingWithWhatsAppDisabledSaysSoInTheResponseAndTheHistory()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var sender = new RecordingIntegrationWhatsAppSender();
        using var host = factory.WithWhatsAppSender(sender);
        var (tenantId, ownerId, bootstrap) = await RegisterTenantAsync(factory, SendPermissions);
        using var _ = bootstrap;
        using var client = CreateClientFor(host, ownerId, tenantId, SendPermissions);
        var quotationId = await CreateSendableQuotationAsync(client, tenantId);
        (await SendAsync(client, tenantId, quotationId)).EnsureSuccessStatusCode();
        (await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"1\"")).EnsureSuccessStatusCode();

        var response = await SendAsync(client, tenantId, quotationId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Disabled", (await JsonAsync(response)).GetProperty("whatsAppOutcome").GetString());
        Assert.Single(sender.Sent);
        var history = await client.GetFromJsonAsync<QuotationHistoryResponse>(
            $"{QuotationsUrl(tenantId)}/{quotationId}/history", TestContext.Current.CancellationToken);
        Assert.NotNull(history);
        Assert.Contains(history.Items, item =>
            item.Details == "Marcada como reenviada sin WhatsApp: el envío por WhatsApp está desactivado para la empresa.");
    }

    [Fact]
    public async Task OwnSendsWithTheTenantTokenNumberAndTemplate()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var zenvia = new CapturingZenviaHandler(HttpStatusCode.OK, """{"id":"zid-1"}""");
        using var host = factory.WithZenviaHandler(zenvia);
        var (tenantId, ownerId, bootstrap) = await RegisterTenantAsync(factory, SendPermissions);
        using var _ = bootstrap;
        using var client = CreateClientFor(host, ownerId, tenantId, SendPermissions);
        (await PutSettingsAsync(client, tenantId, OwnBody(), "\"1\"")).EnsureSuccessStatusCode();
        var quotationId = await CreateSendableQuotationAsync(client, tenantId);

        var response = await SendAsync(client, tenantId, quotationId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Accepted", (await JsonAsync(response)).GetProperty("whatsAppOutcome").GetString());
        var (token, json) = Assert.Single(zenvia.Requests);
        Assert.Equal(SentinelApiKey, token);
        var payload = JsonDocument.Parse(json).RootElement;
        Assert.Equal(FromNumber, payload.GetProperty("from").GetString());
        Assert.Equal(TemplateId, payload.GetProperty("contents")[0].GetProperty("templateId").GetString());
    }

    [Fact]
    public async Task BackToSharedFromDisabledSendsThroughTheGlobalSender()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var sender = new RecordingIntegrationWhatsAppSender();
        using var host = factory.WithWhatsAppSender(sender);
        var (tenantId, ownerId, bootstrap) = await RegisterTenantAsync(factory, SendPermissions);
        using var _ = bootstrap;
        using var client = CreateClientFor(host, ownerId, tenantId, SendPermissions);
        (await PutSettingsAsync(client, tenantId, new { mode = "Disabled" }, "\"1\"")).EnsureSuccessStatusCode();
        (await PutSettingsAsync(client, tenantId, new { mode = "Shared" }, "\"2\"")).EnsureSuccessStatusCode();
        var quotationId = await CreateSendableQuotationAsync(client, tenantId);

        var response = await SendAsync(client, tenantId, quotationId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Accepted", (await JsonAsync(response)).GetProperty("whatsAppOutcome").GetString());
        Assert.Single(sender.Sent);
    }
}
