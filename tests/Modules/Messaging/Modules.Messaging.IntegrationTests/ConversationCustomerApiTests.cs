using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-24 §4.1 (CD-07): dos clientes comparten un número y el resumen de <b>toda</b> respuesta de una sola
/// conversación (detalle, tomar, resolver, reabrir, iniciar) trae al otro en <c>sharedPhoneCustomers</c>; el listado de la bandeja
/// lo manda vacío a propósito.</summary>
public sealed class ConversationCustomerApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Phone = "300 123 4567";

    private const string Catalog = """
        {"data":[{"id":"1","name":"seguimiento","language":"es","status":"APPROVED","category":"MARKETING","parameter_format":"NAMED",
                  "components":[{"type":"BODY","text":"Hola {{first_name}}, ¿sigues interesado?"}]}]}
        """;

    private sealed record Fixture(
        QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, IReadOnlyList<Guid> Customers, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, bool withConversation = true)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var customers = await CreateCustomersAsync(factory, tenant, ("LUIS ALBERTO", Phone), ("ZIBA TIENDA", Phone));
        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        factory.MetaHandler.Respond("/111/messages", HttpStatusCode.OK, """{"messaging_product":"whatsapp","messages":[{"id":"wamid.start.1"}]}""");
        if (withConversation)
        {
            using var anonymous = factory.CreateClient();
            await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", "CO.1", "573001234567", "wamid.in", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
            await DrainDeliveriesAsync(factory);
        }

        return new Fixture(factory, connectionString, tenant, connectionId, customers, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions));
    }

    private static async Task<JsonElement> DetailAsync(Fixture f)
    {
        var id = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        return await f.Client.GetFromJsonAsync<JsonElement>(ConversationUrl(f.Tenant.TenantId, id), Ct);
    }

    /// <summary>El otro cliente del número: el que la conversación no muestra.</summary>
    private static Guid OtherThan(Fixture f, JsonElement summary)
    {
        var shown = summary.GetProperty("customer").GetProperty("id").GetGuid();
        Assert.Contains(shown, f.Customers);
        return f.Customers.Single(id => id != shown);
    }

    private static (Guid Id, string Name, bool IsComplete)[] Shared(JsonElement summary) =>
        [.. summary.GetProperty("sharedPhoneCustomers").EnumerateArray()
            .Select(item => (item.GetProperty("id").GetGuid(), item.GetProperty("name").GetString()!, item.GetProperty("isComplete").GetBoolean()))];

    [Fact]
    public async Task TheDetailListsTheOtherCustomerOfTheNumber()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var detail = await DetailAsync(f);

        var other = OtherThan(f, detail);
        var shared = Assert.Single(Shared(detail));
        Assert.Equal(other, shared.Id);
        Assert.Equal(other == f.Customers[1] ? "ZIBA TIENDA" : "LUIS ALBERTO", shared.Name);
        Assert.True(shared.IsComplete);
    }

    [Fact]
    public async Task WithoutOtherCustomersTheListIsEmptyAndTheInboxAlwaysSendsItEmpty()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var list = await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct);

        // El número es compartido, pero ninguna fila de la bandeja dibuja la línea (D-D6): el listado manda la lista vacía.
        var item = list.GetProperty("items")[0];
        Assert.True(item.TryGetProperty("sharedPhoneCustomers", out var shared));
        Assert.Equal(0, shared.GetArrayLength());
        Assert.Single(Shared(await DetailAsync(f)));
    }

    [Fact]
    public async Task TakingKeepsTheSharedPhoneCustomersInTheAnswer()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var detail = await DetailAsync(f);
        var id = detail.GetProperty("id").GetGuid();

        var taken = await SendAsync(f.Client, HttpMethod.Post, $"{ConversationUrl(f.Tenant.TenantId, id)}/take", ifMatch: $"\"{detail.GetProperty("version").GetInt64()}\"");

        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        var summary = await taken.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal([OtherThan(f, detail)], Shared(summary).Select(item => item.Id));
    }

    [Fact]
    public async Task ResolvingAndReopeningKeepThemToo()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var detail = await DetailAsync(f);
        var url = ConversationUrl(f.Tenant.TenantId, detail.GetProperty("id").GetGuid());
        var other = OtherThan(f, detail);

        var resolved = await SendAsync(f.Client, HttpMethod.Post, $"{url}/resolve", ifMatch: $"\"{detail.GetProperty("version").GetInt64()}\"");
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var resolvedSummary = await resolved.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var reopened = await SendAsync(f.Client, HttpMethod.Post, $"{url}/reopen", ifMatch: $"\"{resolvedSummary.GetProperty("version").GetInt64()}\"");
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        var reopenedSummary = await reopened.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal([other], Shared(resolvedSummary).Select(item => item.Id));
        Assert.Equal([other], Shared(reopenedSummary).Select(item => item.Id));
    }

    // Spec §4.1 y decisión M4: iniciar también devuelve el resumen de una sola conversación.
    [Fact]
    public async Task StartingAConversationAnswersTheSummaryWithTheOtherCustomerOfTheNumber()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, withConversation: false);
        using var host = f.Factory;

        var response = await SendAsync(f.Client, HttpMethod.Post, ConversationsUrl(f.Tenant.TenantId), new
        {
            clientId = Guid.CreateVersion7(),
            connectionId = f.ConnectionId,
            customerId = f.Customers[0],
            template = new { name = "seguimiento", language = "es", parameters = new Dictionary<string, string> { ["first_name"] = "Ana" } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var conversation = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("conversation");
        Assert.Equal(f.Customers[0], conversation.GetProperty("customer").GetProperty("id").GetGuid());
        Assert.Equal([f.Customers[1]], Shared(conversation).Select(item => item.Id));
    }
}
