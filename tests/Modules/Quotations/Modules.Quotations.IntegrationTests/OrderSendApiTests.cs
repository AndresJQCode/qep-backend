using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// <c>POST /orders/{orderId}/send</c>: «Enviar el pedido por WhatsApp». El host de pruebas no llama a
/// Zenvia: el canal y el sender son dobles del harness (<see cref="RecordingWhatsAppChannelDirectory"/>,
/// <see cref="RecordingWhatsAppSender"/>, plan 2026-10-21); el PDF lo dibuja el doble del harness
/// (<see cref="StubPdfRenderer"/>) y la copia pública la publica <c>StubPdfStorage</c>, como en el
/// envío de la cotización.
/// </summary>
public sealed class OrderSendApiTests
{
    private static string SendUrl(Guid tenantId, Guid orderId) =>
        $"/api/v1/tenants/{tenantId}/orders/{orderId}/send";

    private static string ConvertUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/order";

    private static readonly string[] CancellerPermissions =
        [.. ManagerPermissions, OrdersPermissions.OrderCancel];

    // La pantalla repinta el historial con la respuesta: el detalle entero, con la entrada nueva. El
    // nombre del evento viaja tal cual (`Sent`), como el resto del historial.
    [Fact]
    public async Task SendAnswersTheDetailWithASentHistoryEntry()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            SendUrl(tenantId, order.Id), new SendOrderRequest("Customer"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using (var json = JsonDocument.Parse(body))
        {
            var sent = json.RootElement.GetProperty("history").EnumerateArray()
                .Single(entry => entry.GetProperty("eventType").GetString() == "Sent");
            Assert.Equal("Enviado por WhatsApp al cliente.", sent.GetProperty("details").GetString());
            Assert.Equal(JsonValueKind.String, sent.GetProperty("memberName").ValueKind);
            Assert.Equal(order.Id, json.RootElement.GetProperty("order").GetProperty("id").GetGuid());
        }

        // El documento que salió es el del pedido, del mismo layout que la cotización.
        var document = factory.PdfRenderer.LastDocument;
        Assert.NotNull(document);
        Assert.Equal((QuotationPdfKind.Order, order.OrderNumber), (document.Kind, document.QuotationNumber));
        Assert.Null(document.ValidUntil);

        // Enviar no toca el pedido: misma versión, mismo estado.
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"/api/v1/tenants/{tenantId}/orders/{order.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal((order.Version, order.Status), (detail.Order.Version, detail.Order.Status));
        Assert.Contains(detail.History, entry => entry.EventType == "Sent");

        var actions = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Select(ActionOf);
        Assert.Contains("quotation.order.sent", actions);
    }

    // Spec 2026-10-23 D-R3: el documento y el mensaje de WhatsApp salen con el formato numérico del tenant.
    [Fact]
    public async Task TheOrderDocumentAndMessageUseTheTenantSeparators()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);
        await SetTenantRegionalFormatAsync(factory, tenantId, numberFormat: "1,234.56");

        var response = await client.PostAsJsonAsync(
            SendUrl(tenantId, order.Id), new SendOrderRequest("Customer"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = factory.PdfRenderer.LastDocument;
        Assert.NotNull(document);
        Assert.Equal((",", "."), (document.ThousandsSeparator, document.DecimalSeparator));
        Assert.Equal("1,234.56", factory.WhatsAppSender.LastNumberFormat);
    }

    // La lección de `SendWorksWithoutABody` en la cotización: un cuerpo inferido como requerido mata el
    // envío con 500. Sin cuerpo va al cliente.
    [Fact]
    public async Task SendWorksWithoutABody()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);

        var response = await client.PostAsync(
            SendUrl(tenantId, order.Id), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Contains(detail.History, entry => entry.EventType == "Sent");
    }

    [Fact]
    public async Task SendingACancelledOrderIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, CancellerPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);
        (await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders/{order.Id}/cancel",
            new CancelOrderRequest("El cliente desistió"),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.PostAsync(
            SendUrl(tenantId, order.Id), content: null, TestContext.Current.CancellationToken);

        await AssertDomainRejectionAsync(response, "order.order.already_cancelled");
    }

    [Fact]
    public async Task SendingToBillingWithoutABillingPartyIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);

        var billing = await client.PostAsJsonAsync(
            SendUrl(tenantId, order.Id), new SendOrderRequest("Billing"), TestContext.Current.CancellationToken);
        var invalid = await client.PostAsJsonAsync(
            SendUrl(tenantId, order.Id), new SendOrderRequest("Whoever"), TestContext.Current.CancellationToken);

        await AssertDomainRejectionAsync(billing, "order.order.billing_phone_missing");
        await AssertDomainRejectionAsync(invalid, "order.order.recipient_invalid");
    }

    // La política del endpoint: sin `quotations.order.manage` no se envía, aunque se pueda leer el pedido.
    [Fact]
    public async Task SendingWithoutTheManagePermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);
        using var reader = CreateClient(
            factory,
            ownerUserId.ToString(),
            tenantId.ToString(),
            [.. ManagerPermissions.Where(permission => permission != OrdersPermissions.OrderManage)]);

        var response = await reader.PostAsync(
            SendUrl(tenantId, order.Id), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"/api/v1/tenants/{tenantId}/orders/{order.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.DoesNotContain(detail.History, entry => entry.EventType == "Sent");
    }

    [Fact]
    public async Task SendingAnUnknownOrderIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsync(
            SendUrl(tenantId, Guid.CreateVersion7()), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("order.order.not_found", body, StringComparison.Ordinal);
    }

    // Plan 2026-10-21 (Zenvia por tenant): sin canal el pedido no sale y su historial no cambia.
    [Fact]
    public async Task OrderSendWithoutChannelResponds422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        // El pedido nace de una cotización enviada: ese envío ya quedó en el sender.
        var order = await CreateOrderAsync(client, factory, tenantId);
        var sentBefore = factory.WhatsAppSender.Sent.Count;
        factory.WhatsAppChannels.Channels.Clear();

        var response = await client.PostAsJsonAsync(
            SendUrl(tenantId, order.Id), new SendOrderRequest("Customer"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("order.whatsapp.no_channel", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(sentBefore, factory.WhatsAppSender.Sent.Count);
    }

    // Spec 2026-11-09 D6 y D7: una Zenvia sin orderTemplateId no está disponible para pedidos; sin otra es no_channel (ya no
    // not_configured), y una Meta sola sale por Meta sin publicar copia pública.
    [Fact]
    public async Task OrderSendWithoutAnOrderTemplateIsNoChannelAndAMetaChannelAloneSends()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);
        factory.WhatsAppChannels.Channels.Clear();
        factory.WhatsAppChannels.Channels.Add((ZenviaWhatsAppChannel)RecordingWhatsAppChannelDirectory.Default with { OrderTemplateId = null });

        var noChannel = await client.PostAsync(SendUrl(tenantId, order.Id), content: null, TestContext.Current.CancellationToken);
        await AssertDomainRejectionAsync(noChannel, "order.whatsapp.no_channel");

        factory.WhatsAppChannels.Channels.Add(RecordingWhatsAppChannelDirectory.Meta);
        var sentBefore = factory.WhatsAppSender.Sent.Count;
        var response = await client.PostAsync(SendUrl(tenantId, order.Id), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = factory.WhatsAppSender.Sent.Skip(sentBefore).Single();
        Assert.Equal((RecordingWhatsAppChannelDirectory.Meta.ConnectionId, order.OrderNumber), (sent.Channel.ConnectionId, sent.DocumentNumber));
    }

    // Spec 2026-11-09 D7 y D8: con dos canales hace falta connectionId (channel_required); uno que no está es channel_unavailable; el
    // elegido se guarda con el envío y el siguiente no pregunta; otro id después es channel_already_set.
    [Fact]
    public async Task OrderSendWithSeveralChannelsAsksRemembersAndThenRefusesAnotherChoice()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);
        factory.WhatsAppChannels.Channels.Add(RecordingWhatsAppChannelDirectory.Meta);

        var required = await client.PostAsync(SendUrl(tenantId, order.Id), content: null, TestContext.Current.CancellationToken);
        await AssertDomainRejectionAsync(required, "order.whatsapp.channel_required");

        var unavailable = await client.PostAsJsonAsync(
            SendUrl(tenantId, order.Id), new SendOrderRequest(null, Guid.CreateVersion7()), TestContext.Current.CancellationToken);
        await AssertDomainRejectionAsync(unavailable, "order.whatsapp.channel_unavailable");

        var chosen = await client.PostAsJsonAsync(
            SendUrl(tenantId, order.Id), new SendOrderRequest(null, RecordingWhatsAppChannelDirectory.Meta.ConnectionId), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        Assert.Equal(RecordingWhatsAppChannelDirectory.Meta.ConnectionId, factory.WhatsAppSender.Sent[^1].Channel.ConnectionId);
        var actions = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Select(ActionOf);
        Assert.Contains(ChooseWhatsAppChannelHandler.AuditAction, actions);

        // La elección es la misma que la de la cotización: la pantalla de canales ya la reporta.
        var channels = await client.GetFromJsonAsync<JsonElement>(
            $"{QuotationsUrl(tenantId)}/whatsapp-channels?document=order", TestContext.Current.CancellationToken);
        Assert.Equal(RecordingWhatsAppChannelDirectory.Meta.ConnectionId, channels.GetProperty("selectedConnectionId").GetGuid());

        var again = await client.PostAsync(SendUrl(tenantId, order.Id), content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(RecordingWhatsAppChannelDirectory.Meta.ConnectionId, factory.WhatsAppSender.Sent[^1].Channel.ConnectionId);

        var alreadySet = await client.PostAsJsonAsync(
            SendUrl(tenantId, order.Id), new SendOrderRequest(null, RecordingWhatsAppChannelDirectory.Default.ConnectionId), TestContext.Current.CancellationToken);
        await AssertDomainRejectionAsync(alreadySet, "order.whatsapp.channel_already_set");
    }

    private static async Task<OrderResponse> CreateOrderAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, Guid? existingClientId = null)
    {
        var clientId = existingClientId ?? await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var response = await client.PostAsJsonAsync(
            ConvertUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail.Order;
    }

    private static async Task AssertDomainRejectionAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    private static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
    }

    // Spec 2026-11-10 §4.6: el destinatario Contact llega al contacto de la empresa del pedido, con su ficha.
    [Fact]
    public async Task SendingToAContactDeliversToThatContact()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var companyId = await CreateActiveCustomerAsync(client, tenantId);
        var contactId = await CreateActiveCustomerAsync(client, tenantId, name: "Laura Gómez", phone: "311 555 0101");
        await LinkContactAsync(client, tenantId, contactId, companyId);
        var order = await CreateOrderAsync(client, factory, tenantId, companyId);

        var response = await client.PostAsJsonAsync(
            SendUrl(tenantId, order.Id),
            new SendOrderRequest("Contact", ContactId: contactId),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = factory.WhatsAppSender.Messages[^1];
        Assert.Equal(("311 555 0101", "Laura Gómez", (Guid?)contactId), (message.ToPhone, message.FullName, message.CustomerId));
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(TestContext.Current.CancellationToken);
        Assert.Contains(detail!.History, entry => entry.Details == "Enviado por WhatsApp a Laura Gómez.");
    }

    [Fact]
    public async Task AContactOfAnotherClientIs422AndNothingIsSent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var companyId = await CreateActiveCustomerAsync(client, tenantId);
        var otherCompanyId = await CreateActiveCustomerAsync(client, tenantId);
        var strangerId = await CreateActiveCustomerAsync(client, tenantId, name: "Pedro Ríos");
        await LinkContactAsync(client, tenantId, strangerId, otherCompanyId);
        var order = await CreateOrderAsync(client, factory, tenantId, companyId);
        var sentBefore = factory.WhatsAppSender.Messages.Count;

        foreach (var contactId in new Guid?[] { strangerId, companyId, Guid.CreateVersion7(), null })
        {
            var response = await client.PostAsJsonAsync(
                SendUrl(tenantId, order.Id),
                new SendOrderRequest("Contact", ContactId: contactId),
                TestContext.Current.CancellationToken);

            await AssertDomainRejectionAsync(response, "order.order.contact_not_of_client");
        }

        Assert.Equal(sentBefore, factory.WhatsAppSender.Messages.Count);
    }

    [Fact]
    public async Task AContactWithoutPhoneIs422RecipientMissing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var companyId = await CreateActiveCustomerAsync(client, tenantId);
        var contactId = await CreateActiveCustomerAsync(client, tenantId, name: "Laura Gómez", phone: null);
        await LinkContactAsync(client, tenantId, contactId, companyId);
        var order = await CreateOrderAsync(client, factory, tenantId, companyId);

        var response = await client.PostAsJsonAsync(
            SendUrl(tenantId, order.Id),
            new SendOrderRequest("Contact", ContactId: contactId),
            TestContext.Current.CancellationToken);

        await AssertDomainRejectionAsync(response, "order.whatsapp.recipient_missing");
    }

    [Fact]
    public async Task TheOrderDetailListsTheClientsContacts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var companyId = await CreateActiveCustomerAsync(client, tenantId);
        var contactId = await CreateActiveCustomerAsync(client, tenantId, name: "Laura Gómez", phone: "311 555 0101");
        await LinkContactAsync(client, tenantId, contactId, companyId);
        var order = await CreateOrderAsync(client, factory, tenantId, companyId);

        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"/api/v1/tenants/{tenantId}/orders/{order.Id}", TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        var contact = Assert.Single(detail.ClientContacts);
        Assert.Equal((contactId, "Laura Gómez", "311 555 0101"), (contact.Id, contact.Name, contact.Phone));
    }
}
