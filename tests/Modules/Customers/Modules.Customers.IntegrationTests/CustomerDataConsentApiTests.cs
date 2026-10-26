using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Testcontainers.PostgreSql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// Spec 2026-10-26 D-8 y D-9: <c>GET …/data-consent</c> devuelve el estado vigente, si el llamador puede revocar y el historial
/// completo del más nuevo al más viejo, con el nombre de quien registró cada fila. Las filas del bot nacen por
/// <see cref="ICustomerDataConsents"/>; los clientes, por la API.
/// </summary>
public sealed class CustomerDataConsentApiTests
{
    private const string SharedNumber = "92-1396892";
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly Guid ChatbotId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private sealed class Fixture(PostgreSqlContainer database, QepApiFactory factory, HttpClient client, Guid customerId)
        : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public Guid CustomerId { get; } = customerId;

        public string Url(string tenantId = TenantId, Guid? id = null) =>
            $"{CustomersUrl(tenantId)}/{id ?? CustomerId}/data-consent";

        public HttpClient ClientWith(params string[] permissions) =>
            CreateClient(factory, SubjectId, TenantId, permissions);

        public HttpClient OtherTenantClient() =>
            CreateClient(factory, OtherSubjectId, OtherTenantId, CustomersPermissions.CustomerRead);

        public static async Task<Fixture> StartAsync()
        {
            var database = await StartDatabaseAsync();
            var factory = new QepApiFactory(database.GetConnectionString());
            await SeedTenantAsync(factory);
            var client = CreateClient(
                factory, SubjectId, TenantId,
                CustomersPermissions.CustomerRead,
                CustomersPermissions.CustomerManage,
                CustomersPermissions.ClassificationRead,
                CustomersPermissions.ClassificationManage);
            var city = await EnsureCityAsync(client);
            var classification = await CreateClassificationAsync(client);
            var response = await client.PostAsJsonAsync(
                CustomersUrl(),
                NewCustomerBody(city.CityId, classification.Id, "GYSELYS", "NIT", SharedNumber),
                TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var id = (await response.Content.ReadFromJsonAsync<CustomerResponse>(TestContext.Current.CancellationToken))!.Id;
            return new Fixture(database, factory, client, id);
        }

        public async Task RecordAsync(DataConsentEvent decision, DateTimeOffset at)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var consents = scope.ServiceProvider.GetRequiredService<ICustomerDataConsents>();
            var outcome = await consents.RecordFromChatbotAsync(
                new ChatbotDataConsentRecord(
                    Tenant,
                    CustomerId,
                    decision,
                    at,
                    new DataConsentIdentity(Guid.CreateVersion7(), DataConsentChannel.WhatsApp, "CO.123456789", "573001234567"),
                    new ChatbotConsentEvidence(
                        "¿Aceptas nuestra política de datos?", Guid.CreateVersion7(), Guid.CreateVersion7(), "wamid.HBgM",
                        ChatbotId, "Bienvenida", Guid.CreateVersion7(), 3, "n4")),
                TestContext.Current.CancellationToken);
            Assert.Equal(DataConsentRecordOutcome.Recorded, outcome);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task WithoutRowsTheCurrentIsNull()
    {
        await using var fixture = await Fixture.StartAsync();

        var body = await BodyAsync(
            await fixture.Client.GetAsync(fixture.Url(), TestContext.Current.CancellationToken), HttpStatusCode.OK);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("current").ValueKind);
        Assert.Equal(0, body.GetProperty("history").GetArrayLength());
        Assert.False(body.GetProperty("canRevoke").GetBoolean());
    }

    [Fact]
    public async Task TheHistoryIsNewestFirstWithTheActorNames()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.RecordAsync(DataConsentEvent.Rejected, Start);
        await fixture.RecordAsync(DataConsentEvent.Accepted, Start.AddMinutes(5));

        var body = await BodyAsync(
            await fixture.Client.GetAsync(fixture.Url(), TestContext.Current.CancellationToken), HttpStatusCode.OK);

        var current = body.GetProperty("current");
        Assert.Equal("Accepted", current.GetProperty("event").GetString());
        Assert.Equal("WhatsApp", current.GetProperty("channel").GetString());
        var history = body.GetProperty("history");
        Assert.Equal(2, history.GetArrayLength());
        Assert.Equal("Accepted", history[0].GetProperty("event").GetString());
        Assert.Equal("Rejected", history[1].GetProperty("event").GetString());
        var actor = history[0].GetProperty("actor");
        Assert.Equal("Chatbot", actor.GetProperty("kind").GetString());
        Assert.Equal(ChatbotId, actor.GetProperty("id").GetGuid());
        Assert.Equal("Bienvenida", actor.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("medium").ValueKind);
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task CanRevokeNeedsTheAcceptanceAndThePermission()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.RecordAsync(DataConsentEvent.Accepted, Start);

        var withPermission = await BodyAsync(
            await fixture.ClientWith(CustomersPermissions.CustomerRead, CustomersPermissions.DataConsentRevoke)
                .GetAsync(fixture.Url(), TestContext.Current.CancellationToken),
            HttpStatusCode.OK);
        var readOnly = await BodyAsync(
            await fixture.ClientWith(CustomersPermissions.CustomerRead)
                .GetAsync(fixture.Url(), TestContext.Current.CancellationToken),
            HttpStatusCode.OK);

        Assert.True(withPermission.GetProperty("canRevoke").GetBoolean());
        Assert.False(readOnly.GetProperty("canRevoke").GetBoolean());
    }

    [Fact]
    public async Task ReadingNeedsCustomerReadAndTheTenant()
    {
        await using var fixture = await Fixture.StartAsync();

        var noPermission = await fixture.ClientWith(CustomersPermissions.DataConsentRevoke)
            .GetAsync(fixture.Url(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, noPermission.StatusCode);

        var otherTenant = await BodyAsync(
            await fixture.OtherTenantClient().GetAsync(fixture.Url(), TestContext.Current.CancellationToken),
            HttpStatusCode.Forbidden);
        Assert.Equal("authorization.denied", otherTenant.GetProperty("code").GetString());

        var missing = await BodyAsync(
            await fixture.Client.GetAsync(fixture.Url(id: Guid.CreateVersion7()), TestContext.Current.CancellationToken),
            HttpStatusCode.NotFound);
        Assert.Equal("customers.customer.not_found", missing.GetProperty("code").GetString());
    }
}
