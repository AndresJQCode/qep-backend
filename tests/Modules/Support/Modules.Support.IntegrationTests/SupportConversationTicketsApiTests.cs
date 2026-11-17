using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Support.Application;
using Modules.Tenancy.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportTicketScenario;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Soporte fase 2 (Tarea 7, plan E10): abrir un ticket desde una conversación de Mensajería y la pestaña de Soporte de su ficha. La
/// conversación se siembra por SQL con su cliente, como las pruebas que leen conversaciones en otros módulos. Host compartido: cada
/// prueba siembra su tenant y sólo lee lo suyo.
/// </summary>
public sealed class SupportConversationTicketsApiTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATicketFromAConversationLinksItsCustomerAndFollowsTheThread()
    {
        var scenario = await StartAsync(await sharedHost.StartTestAsync());
        var customer = await SeedCustomerAsync(scenario.Factory, scenario.TenantId, "Ana Gómez", "+573001234567", "ana@example.com");
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId, customerId: customer);

        var ticket = await CreateFromConversationAsync(scenario, conversation);

        Assert.Equal(customer, ticket.GetProperty("customer").GetProperty("id").GetGuid());
        Assert.Equal("Conversation", ticket.GetProperty("intake").GetString());
        Assert.Equal("Thread", ticket.GetProperty("replyChannel").GetString());
        Assert.Equal(JsonValueKind.Null, ticket.GetProperty("manualMedium").ValueKind);
        Assert.Equal(conversation, ticket.GetProperty("conversationId").GetGuid());
        Assert.Equal("WhatsApp", ticket.GetProperty("conversationChannel").GetString());
        Assert.True(ticket.GetProperty("followsThread").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ticket.GetProperty("followOffAt").ValueKind);
        Assert.Equal(0, ticket.GetProperty("unclassifiedCount").GetInt32());
        Assert.Empty(ticket.GetProperty("replies").EnumerateArray());
        // El contacto del cliente llena la foto (regla F1 de la fase 1): su teléfono va al fijo (owner, 2026-11-15, P3) y el celular
        // es el wa_id de la conversación.
        Assert.Equal("ana@example.com", ticket.GetProperty("requester").GetProperty("email").GetString());
        Assert.Equal("+573001234567", ticket.GetProperty("requester").GetProperty("landlinePhone").GetString());
        Assert.Equal("+573001234567", ticket.GetProperty("requester").GetProperty("mobilePhone").GetString());
        Assert.Equal(1, await CountAuditEntriesAsync(scenario.Factory, SupportAuditActions.TicketCreated, Id(ticket).ToString()));
    }

    [Fact]
    public async Task ATicketFromAConversationTakesItsPriority()
    {
        var scenario = await StartAsync(await sharedHost.StartTestAsync());
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId);
        var url = $"{scenario.Url}/conversations/{conversation}/tickets";
        var typeId = await scenario.TypeIdAsync("Petición general");

        var low = await scenario.Client.PostAsJsonAsync(
            url, new { ticketTypeId = typeId, subject = "Pedido sin llegar", description = "No ha llegado.", priority = "Low" }, Ct);
        var lowercase = await scenario.Client.PostAsJsonAsync(
            url, new { ticketTypeId = typeId, subject = "Pedido sin llegar", description = "No ha llegado.", priority = "high" }, Ct);
        var absent = await CreateFromConversationAsync(scenario, conversation);

        Assert.True(low.StatusCode == HttpStatusCode.Created, await BodyAsync(low));
        Assert.Equal("Low", (await JsonAsync(low)).GetProperty("priority").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, lowercase.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(lowercase));
        Assert.Equal(["Priority"], await ErrorKeysAsync(lowercase));
        Assert.Equal("Medium", absent.GetProperty("priority").GetString());
    }

    [Fact]
    public async Task ATicketFromAConversationWritesTicketOpenedInTheThread()
    {
        var scenario = await StartAsync(await sharedHost.StartTestAsync());
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId);

        var ticket = await CreateFromConversationAsync(scenario, conversation);

        Assert.Equal(1, await CountTicketEventsAsync(scenario.Factory, conversation, Id(ticket), "TicketOpened"));
        Assert.Equal(
            scenario.CallerMemberId.ToString(),
            await TextAsync(
                scenario.Factory,
                "SELECT details->>'actor' FROM messaging.messages WHERE conversation_id = @id AND details->>'type' = 'TicketOpened'",
                ("id", conversation)));
    }

    [Fact]
    public async Task AWhatsAppConversationGivesTheRequesterItsMobile()
    {
        var scenario = await StartAsync(await sharedHost.StartTestAsync());
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId, waId: "573001234567", profileName: "Ana Gómez");

        var ticket = await CreateFromConversationAsync(scenario, conversation);

        Assert.Equal(JsonValueKind.Null, ticket.GetProperty("customer").ValueKind);
        var requester = ticket.GetProperty("requester");
        Assert.Equal("Ana Gómez", requester.GetProperty("firstNames").GetString());
        Assert.Equal("+573001234567", requester.GetProperty("mobilePhone").GetString());
        Assert.Equal("CO", requester.GetProperty("countryCode").GetString());
    }

    [Fact]
    public async Task AnInstagramConversationWithoutCustomerUsesItsUsername()
    {
        var scenario = await StartAsync(await sharedHost.StartTestAsync());
        var conversation = await SeedConversationAsync(
            scenario.Factory, scenario.TenantId, channel: "Instagram", waId: null, userId: "17841400000000002", profileName: null,
            username: "ana.gomez");

        var ticket = await CreateFromConversationAsync(scenario, conversation);

        Assert.Equal("Instagram", ticket.GetProperty("conversationChannel").GetString());
        var requester = ticket.GetProperty("requester");
        Assert.Equal("ana.gomez", requester.GetProperty("firstNames").GetString());
        Assert.Equal(JsonValueKind.Null, requester.GetProperty("mobilePhone").ValueKind);
    }

    [Fact]
    public async Task MessagingOffIs403()
    {
        var scenario = await StartAsync(
            await sharedHost.StartTestAsync(), TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Messaging));
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId);

        var response = await PostFromConversationAsync(scenario, conversation);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", await CodeOfAsync(response));
        Assert.Equal(0, await CountTicketEventsCountAsync(scenario, conversation));
    }

    [Fact]
    public async Task AConversationOfAnotherTenantIs404()
    {
        var scenario = await StartAsync(await sharedHost.StartTestAsync());
        var other = NewTenantId();
        await SeedTenantAsync(scenario.Factory, other);
        var conversation = await SeedConversationAsync(scenario.Factory, Guid.Parse(other));

        var response = await PostFromConversationAsync(scenario, conversation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("support.conversation.not_found", await CodeOfAsync(response));
    }

    [Fact]
    public async Task TheContactTabListsThisConversationAndTheSameCustomerTickets()
    {
        var scenario = await StartAsync(await sharedHost.StartTestAsync());
        var customer = await SeedCustomerAsync(scenario.Factory, scenario.TenantId, "Ana Gómez", "+573001234567", "ana@example.com");
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId, customerId: customer);
        var stranger = await SeedConversationAsync(scenario.Factory, scenario.TenantId, waId: "573009998877", profileName: "Beto");
        var fromHere = await CreateFromConversationAsync(scenario, conversation);
        var byEmail = await scenario.CreateAsync(customerId: customer);
        await CreateFromConversationAsync(scenario, stranger);

        var response = await scenario.Client.GetAsync($"{scenario.Url}/conversations/{conversation}/tickets", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tab = await JsonAsync(response);
        Assert.Equal(conversation, tab.GetProperty("conversationId").GetGuid());
        Assert.True(tab.GetProperty("canCreate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, tab.GetProperty("cannotCreateReason").ValueKind);
        Assert.Equal(0, tab.GetProperty("unclassifiedCount").GetInt32());
        var items = tab.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("ticket").GetProperty("id").GetGuid());
        Assert.Equal(2, items.Count);
        Assert.True(items[Id(fromHere)].GetProperty("isThisConversation").GetBoolean());
        Assert.True(items[Id(fromHere)].GetProperty("followsThread").GetBoolean());
        Assert.False(items[Id(byEmail)].GetProperty("isThisConversation").GetBoolean());
        Assert.False(items[Id(byEmail)].GetProperty("followsThread").GetBoolean());
    }

    [Fact]
    public async Task TheContactTabSaysWhyItCannotCreate()
    {
        var scenario = await StartAsync(
            await sharedHost.StartTestAsync(), TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Messaging));
        var conversation = await SeedConversationAsync(scenario.Factory, scenario.TenantId);

        var withoutMessaging = await JsonAsync(await scenario.Client.GetAsync($"{scenario.Url}/conversations/{conversation}/tickets", Ct));

        Assert.False(withoutMessaging.GetProperty("canCreate").GetBoolean());
        Assert.Equal("messaging_disabled", withoutMessaging.GetProperty("cannotCreateReason").GetString());

        var withMessaging = await StartAsync(await sharedHost.StartTestAsync());
        var other = await SeedConversationAsync(withMessaging.Factory, withMessaging.TenantId);
        var reader = CreateClient(withMessaging.Factory, withMessaging.SubjectId, withMessaging.Tenant, SupportPermissions.Read);

        var readOnly = await JsonAsync(await reader.GetAsync($"{withMessaging.Url}/conversations/{other}/tickets", Ct));

        Assert.False(readOnly.GetProperty("canCreate").GetBoolean());
        Assert.Equal("no_permission", readOnly.GetProperty("cannotCreateReason").GetString());
    }

    private static async Task<JsonElement> CreateFromConversationAsync(SupportTicketScenario scenario, Guid conversation)
    {
        var response = await PostFromConversationAsync(scenario, conversation);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        var ticket = await JsonAsync(response);
        Assert.Equal($"{scenario.Url}/tickets/{Id(ticket)}", response.Headers.Location?.OriginalString);
        return ticket;
    }

    private static async Task<HttpResponseMessage> PostFromConversationAsync(SupportTicketScenario scenario, Guid conversation) =>
        await scenario.Client.PostAsJsonAsync(
            $"{scenario.Url}/conversations/{conversation}/tickets",
            new
            {
                ticketTypeId = await scenario.TypeIdAsync("Petición general"),
                subject = "Pedido sin llegar",
                description = "El pedido de la semana pasada no ha llegado.",
            },
            Ct);

    private static Task<long> CountTicketEventsCountAsync(SupportTicketScenario scenario, Guid conversation) =>
        ScalarAsync(
            scenario.Factory,
            "SELECT count(*) FROM messaging.messages WHERE conversation_id = @id AND direction = 3",
            ("id", conversation));
}
