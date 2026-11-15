using System.Net;
using System.Text.Json;
using Modules.Support.Application;
using Modules.Tenancy.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportTicketScenario;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// El alta manual, el detalle y los responsables. Host propio de la clase con el reloj fijado el viernes 2026-11-13 a las 10:00
/// de Bogotá (<see cref="ClockedSupportHost"/>); cada prueba siembra su tenant y su usuario.
/// </summary>
public sealed class SupportTicketsApiTests(ClockedSupportHost host) : IClassFixture<ClockedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreatingAManualTicketReturnsSOP000001()
    {
        var scenario = await StartAsync(await host.StartTestAsync());

        var response = await scenario.PostAsync(Body(await scenario.TypeIdAsync("Petición general")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ticket = await JsonAsync(response);
        Assert.Equal($"{scenario.Url}/tickets/{Id(ticket)}", response.Headers.Location?.OriginalString);
        Assert.Equal("SOP-000001", ticket.GetProperty("number").GetString());
        Assert.Equal("New", ticket.GetProperty("status").GetString());
        Assert.Equal(["Open", "WaitingOnCustomer", "Resolved"], Strings(ticket.GetProperty("allowedTransitions")));
        var clock = ticket.GetProperty("clock");
        Assert.Equal("2026-12-07", clock.GetProperty("dueOn").GetString());
        Assert.Equal("OnTrack", clock.GetProperty("dueState").GetString());
        Assert.Equal(15, clock.GetProperty("businessDaysLeft").GetInt32());
        Assert.Equal(15, clock.GetProperty("termBusinessDays").GetInt32());
        Assert.True(clock.GetProperty("termIsLegal").GetBoolean());
        Assert.Equal(0, clock.GetProperty("pausedBusinessDays").GetInt32());
        Assert.Equal("Manual", ticket.GetProperty("intake").GetString());
        Assert.Equal("Phone", ticket.GetProperty("manualMedium").GetString());
        Assert.Equal("Email", ticket.GetProperty("replyChannel").GetString());
        Assert.Equal("Petición general", ticket.GetProperty("type").GetProperty("name").GetString());
        Assert.Equal("Ana", ticket.GetProperty("requester").GetProperty("firstNames").GetString());
        Assert.Equal("CO", ticket.GetProperty("requester").GetProperty("countryCode").GetString());
        Assert.Equal(JsonValueKind.Null, ticket.GetProperty("customer").ValueKind);
        Assert.Equal(JsonValueKind.Null, ticket.GetProperty("assignee").ValueKind);
        Assert.Equal(0, ticket.GetProperty("resolutionCount").GetInt32());
        Assert.Equal(1L, ticket.GetProperty("version").GetInt64());
        var created = Assert.Single(ticket.GetProperty("events").EnumerateArray());
        Assert.Equal(1, created.GetProperty("ordinal").GetInt32());
        Assert.Equal("Created", created.GetProperty("kind").GetString());
        Assert.Equal("New", created.GetProperty("toStatus").GetString());
        Assert.Equal(scenario.CallerMemberId, created.GetProperty("actor").GetProperty("id").GetGuid());
        Assert.Equal("Zoe Llamante", created.GetProperty("actor").GetProperty("displayName").GetString());
        Assert.Equal(1, await CountAuditEntriesAsync(scenario.Factory, SupportAuditActions.TicketCreated, Id(ticket).ToString()));
        var detail = await scenario.DetailAsync(Id(ticket));
        Assert.Equal("SOP-000001", detail.GetProperty("number").GetString());
        Assert.Equal("2026-12-07", detail.GetProperty("clock").GetProperty("dueOn").GetString());
    }

    [Fact]
    public async Task ConcurrentCreationsGetDistinctConsecutiveNumbers()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var typeId = await scenario.TypeIdAsync("Queja");

        var responses = await Task.WhenAll(Enumerable.Range(1, 10).Select(index => scenario.PostAsync(Body(typeId, subject: $"Queja {index}"))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var numbers = new List<string>();
        foreach (var response in responses)
        {
            numbers.Add((await JsonAsync(response)).GetProperty("number").GetString()!);
        }

        Assert.Equal(Enumerable.Range(1, 10).Select(number => $"SOP-{number:D6}"), numbers.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WithoutCustomerOrRequesterNameIs422()
    {
        var scenario = await StartAsync(await host.StartTestAsync());

        var response = await scenario.PostAsync(Body(await scenario.TypeIdAsync("Queja"), requester: new { email = "sin-nombre@example.com" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.ticket.requester_required", await CodeOfAsync(response));
    }

    [Fact]
    public async Task ACustomerOfAnotherTenantIs422NotFound()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var other = await StartAsync(scenario.Factory);
        var foreign = await SeedCustomerAsync(scenario.Factory, other.TenantId, "Novasteel SAS");

        var response = await scenario.PostAsync(Body(await scenario.TypeIdAsync("Queja"), customerId: foreign));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.ticket.customer_not_found", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AnInactiveCustomerIs422()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var customer = await SeedCustomerAsync(scenario.Factory, scenario.TenantId, "Novasteel SAS");
        await DeactivateCustomerAsync(scenario.Factory, customer);

        var response = await scenario.PostAsync(Body(await scenario.TypeIdAsync("Queja"), customerId: customer));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.ticket.customer_inactive", await CodeOfAsync(response));
    }

    [Fact]
    public async Task ALinkedCustomerNamesTheTicket()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var customer = await SeedCustomerAsync(scenario.Factory, scenario.TenantId, "Novasteel SAS");

        var ticket = await scenario.CreateAsync(Body(await scenario.TypeIdAsync("Queja"), customerId: customer));

        Assert.Equal(customer, ticket.GetProperty("customer").GetProperty("id").GetGuid());
        Assert.Equal("Novasteel SAS", ticket.GetProperty("customer").GetProperty("name").GetString());
        Assert.True(ticket.GetProperty("customer").GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task ACustomerWithTheCustomersModuleOffIs403()
    {
        var scenario = await StartAsync(
            await host.StartTestAsync(), TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Customers));
        var customer = await SeedCustomerAsync(scenario.Factory, scenario.TenantId, "Novasteel SAS");

        var response = await scenario.PostAsync(Body(await scenario.TypeIdAsync("Queja"), customerId: customer));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", await CodeOfAsync(response));
    }

    [Fact]
    public async Task TheThreadChannelIsUnavailableForAManualTicket()
    {
        var scenario = await StartAsync(await host.StartTestAsync());

        var response = await scenario.PostAsync(Body(await scenario.TypeIdAsync("Queja"), replyChannel: "Thread"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.ticket.reply_channel_unavailable", await CodeOfAsync(response));
    }

    [Fact]
    public async Task TheEmailChannelNeedsAnEmail()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var typeId = await scenario.TypeIdAsync("Queja");

        var withoutEmail = await scenario.PostAsync(Body(typeId, requester: new { firstNames = "Ana", mobilePhone = "+573001234567" }));
        var withoutMobile = await scenario.PostAsync(
            Body(typeId, requester: new { firstNames = "Ana", email = "ana@example.com" }, replyChannel: "WhatsAppTemplate"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutEmail.StatusCode);
        Assert.Equal("support.ticket.reply_channel_unavailable", await CodeOfAsync(withoutEmail));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutMobile.StatusCode);
        Assert.Equal("support.ticket.reply_channel_unavailable", await CodeOfAsync(withoutMobile));
    }

    /// <summary>Revisión final I1: el canal se valida sólo contra el correo y el celular del solicitante. El contacto del cliente
    /// vinculado no cuenta en esta fase (el puerto de clientes no lo expone); cambiarlo es decisión pendiente del owner.</summary>
    [Fact]
    public async Task ALinkedCustomerWithoutRequesterContactIs422ForEmail()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var customer = await SeedCustomerAsync(scenario.Factory, scenario.TenantId, "Novasteel SAS");
        var typeId = await scenario.TypeIdAsync("Queja");
        var withoutRequester = Body(typeId, customerId: customer);
        withoutRequester["requester"] = null;

        var onlyCustomer = await scenario.PostAsync(withoutRequester);
        var requesterWithoutContact = await scenario.PostAsync(
            Body(typeId, requester: new { firstNames = "Ana" }, customerId: customer));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, onlyCustomer.StatusCode);
        Assert.Equal("support.ticket.reply_channel_unavailable", await CodeOfAsync(onlyCustomer));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, requesterWithoutContact.StatusCode);
        Assert.Equal("support.ticket.reply_channel_unavailable", await CodeOfAsync(requesterWithoutContact));
    }

    [Fact]
    public async Task AnInactiveTypeIs422()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var typeId = await scenario.TypeIdAsync("Sugerencia");
        var deactivated = await PutAsync(
            scenario.Client,
            $"{scenario.Url}/settings/ticket-types/{typeId}",
            new { name = "Sugerencia", termBusinessDays = 15, termIsLegal = true, requiresFormalResponse = false, isPublished = true, isActive = false },
            version: 1);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        var response = await scenario.PostAsync(Body(typeId));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.ticket_type.unavailable", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AnAssigneeWhoCannotManageIs422()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var billing = await SeedMemberAsync(scenario.Factory, $"billing-{scenario.Tenant}@example.com", "Caro Cobros", "billing", tenantId: scenario.Tenant);

        var response = await scenario.PostAsync(Body(await scenario.TypeIdAsync("Queja"), assignee: billing));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.ticket.assignee_invalid", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AnAssigneeWhoCanManageIsSaved()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var advisor = await SeedMemberAsync(scenario.Factory, $"advisor-{scenario.Tenant}@example.com", "Beto Asesor", "advisor", tenantId: scenario.Tenant);

        var ticket = await scenario.CreateAsync(Body(await scenario.TypeIdAsync("Queja"), assignee: advisor));

        Assert.Equal(advisor, ticket.GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.Equal("Beto Asesor", ticket.GetProperty("assignee").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task TheDetailOfATicketOfAnotherTenantUnderMyRouteIs404()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var other = await StartAsync(scenario.Factory);
        var foreign = await other.CreateAsync();

        var response = await scenario.Client.GetAsync($"{scenario.Url}/tickets/{Id(foreign)}", Ct);
        var missing = await scenario.Client.GetAsync($"{scenario.Url}/tickets/{Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("support.ticket.not_found", await CodeOfAsync(response));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("support.ticket.not_found", await CodeOfAsync(missing));
    }

    [Fact]
    public async Task TheAssigneesAreTheActiveManagers()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var admin = await SeedMemberAsync(scenario.Factory, $"admin-{scenario.Tenant}@example.com", "Ana Admin", "admin", tenantId: scenario.Tenant);
        var advisor = await SeedMemberAsync(scenario.Factory, $"advisor-{scenario.Tenant}@example.com", "Beto Asesor", "advisor", tenantId: scenario.Tenant);
        await SeedMemberAsync(scenario.Factory, $"billing-{scenario.Tenant}@example.com", "Caro Cobros", "billing", tenantId: scenario.Tenant);
        await SeedMemberAsync(scenario.Factory, $"removed-{scenario.Tenant}@example.com", "Dora Removida", "admin", removed: true, tenantId: scenario.Tenant);

        var response = await scenario.Client.GetAsync($"{scenario.Url}/assignees", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await JsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            [(admin, "Ana Admin"), (advisor, "Beto Asesor"), (scenario.CallerMemberId, "Zoe Llamante")],
            items.Select(item => (item.GetProperty("id").GetGuid(), item.GetProperty("displayName").GetString()!)));
    }

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!).ToList();
}
