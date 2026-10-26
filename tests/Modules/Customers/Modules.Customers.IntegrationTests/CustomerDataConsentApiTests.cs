using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>
/// Spec 2026-10-26 D-8 y D-9: <c>GET …/data-consent</c> devuelve el estado vigente, si el llamador puede revocar y el historial
/// completo del más nuevo al más viejo, con el nombre de quien registró cada fila; <c>POST …/data-consent/revocations</c> registra
/// la revocación de un miembro (D-5 <c>medium</c>) y responde lo mismo. Las filas del bot nacen por
/// <see cref="ICustomerDataConsents"/>; los clientes, por la API. Las conversaciones (Mensajería) y los nombres de los miembros
/// (Tenancy) son dobles.
/// </summary>
public sealed class CustomerDataConsentApiTests
{
    private const string SharedNumber = "92-1396892";
    private const string MemberName = "Ana Ruiz";

    // Un miembro del tenant que el doble de nombres no conoce: el que ya no existe.
    private const string GoneSubjectId = "01900000-0000-7000-8000-0000000000d7";
    private const string NotAccepted = "customers.data_consent.not_accepted";
    private const string ConversationInvalid = "customers.data_consent.conversation_invalid";
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly Guid ChatbotId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly object ByOtherMedium = new { medium = "Other", conversationId = (Guid?)null, note = "Por correo." };

    /// <summary>Las conversaciones del tenant que el doble conoce; cualquier otra «no existe».</summary>
    private sealed class FakeConversations : ICustomerConversations
    {
        public Dictionary<Guid, CustomerConversationRef> Rows { get; } = [];

        public Task<CustomerConversationRef?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            Task.FromResult(tenantId == Tenant && Rows.TryGetValue(conversationId, out var row) ? row : null);
    }

    /// <summary>Sólo <see cref="CustomersApiHarness.SubjectId"/> tiene nombre; cualquier otro usuario «ya no existe».</summary>
    private sealed class FakeAuthorNames : ICustomerAuthorNames
    {
        public Task<IReadOnlyDictionary<Guid, string>> FindAsync(
            Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(userIds
                .Where(id => tenantId == Tenant && id == Guid.Parse(SubjectId))
                .Distinct()
                .ToDictionary(id => id, _ => MemberName));
    }

    private sealed class Fixture(
        PostgreSqlContainer database, QepApiFactory factory, HttpClient client, Guid customerId, Guid cityId, Guid classificationId)
        : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public Guid CustomerId { get; } = customerId;

        public string ConnectionString => database.GetConnectionString();

        /// <summary>Del cliente, por WhatsApp, con identidad completa.</summary>
        public Guid Conversation { get; } = Guid.CreateVersion7();

        /// <summary>De otro cliente.</summary>
        public Guid ForeignConversation { get; } = Guid.CreateVersion7();

        /// <summary>Del cliente, pero sin identidad de canal.</summary>
        public Guid AnonymousConversation { get; } = Guid.CreateVersion7();

        public string Url(string tenantId = TenantId, Guid? id = null) =>
            $"{CustomersUrl(tenantId)}/{id ?? CustomerId}/data-consent";

        public HttpClient ClientWith(params string[] permissions) =>
            CreateClient(factory, SubjectId, TenantId, permissions);

        public HttpClient Revoker(string subjectId = SubjectId) =>
            CreateClient(factory, subjectId, TenantId, CustomersPermissions.CustomerRead, CustomersPermissions.DataConsentRevoke);

        public HttpClient OtherTenantClient() =>
            CreateClient(factory, OtherSubjectId, OtherTenantId, CustomersPermissions.CustomerRead);

        public Task<CustomerResponse> CreateOtherCustomerAsync() =>
            CreateCustomerAsync(Client, cityId, classificationId, "El Que Queda", "830.222.222-2");

        public static async Task<Fixture> StartAsync()
        {
            var database = await StartDatabaseAsync();
            var conversations = new FakeConversations();
            var factory = new QepApiFactory(database.GetConnectionString(), services =>
            {
                services.RemoveAll<ICustomerConversations>();
                services.AddSingleton<ICustomerConversations>(conversations);
                services.RemoveAll<ICustomerAuthorNames>();
                services.AddSingleton<ICustomerAuthorNames, FakeAuthorNames>();
            });
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
            var fixture = new Fixture(database, factory, client, id, city.CityId, classification.Id);
            conversations.Rows[fixture.Conversation] =
                new CustomerConversationRef(fixture.Conversation, id, "WhatsApp", "CO.laura1", "573001234567");
            conversations.Rows[fixture.ForeignConversation] =
                new CustomerConversationRef(fixture.ForeignConversation, Guid.CreateVersion7(), "WhatsApp", "CO.pedro2", "573009876543");
            conversations.Rows[fixture.AnonymousConversation] =
                new CustomerConversationRef(fixture.AnonymousConversation, id, "WhatsApp", null, "573001234567");
            return fixture;
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

        /// <summary>Una respuesta del bot anterior a la revocación, que lleva la hora real del servidor.</summary>
        public Task RecordBeforeNowAsync(DataConsentEvent decision) => RecordAsync(decision, DateTimeOffset.UtcNow.AddMinutes(-30));

        public Task<HttpResponseMessage> RevokeAsync(object body, HttpClient? client = null) =>
            (client ?? Revoker()).PostAsJsonAsync($"{Url()}/revocations", body, TestContext.Current.CancellationToken);

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

    private static async Task<string?> TextAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountAsync(string connectionString, string sql) =>
        long.Parse((await TextAsync(connectionString, sql))!, CultureInfo.InvariantCulture);

    private static Task<long> ConsentRowsAsync(string connectionString) =>
        CountAsync(connectionString, "SELECT count(*) FROM customers.data_consents");

    // Las columnas de la única revocación, en una línea («-» = NULL), para compararlas de una vez.
    private static Task<string?> RevocationRowAsync(string connectionString) =>
        TextAsync(
            connectionString,
            """
            SELECT concat_ws('|', coalesce(conversation_id::text, '-'), coalesce(channel, '-'), coalesce(channel_user_id, '-'),
                coalesce(wa_id, '-'), actor_kind, coalesce(actor_user_id::text, '-'), medium, note)
            FROM customers.data_consents WHERE event = 'Revoked'
            """);

    private static Task<long> RevocationAuditsAsync(string connectionString, Guid customerId) =>
        CountAsync(
            connectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'platform.audit.recorded.v1' "
            + $"AND payload->>'action' = '{CustomerAuditActions.DataConsentRevoked}' AND payload->>'resourceId' = '{customerId}' "
            + $"AND payload->>'actorType' = 'Human' AND payload->>'actorId' = '{SubjectId}'");

    private static void AssertActor(JsonElement row, string userId, string name)
    {
        var actor = row.GetProperty("actor");
        Assert.Equal("Member", actor.GetProperty("kind").GetString());
        Assert.Equal(Guid.Parse(userId), actor.GetProperty("id").GetGuid());
        Assert.Equal(name, actor.GetProperty("name").GetString());
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

    // ---- Revocar (spec 2026-10-26 D-5 medium, D-8, D-9) ---------------------------------------------------------

    [Fact]
    public async Task RevokingByThisConversationStoresItsIdentity()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.RecordBeforeNowAsync(DataConsentEvent.Accepted);

        var body = await BodyAsync(
            await fixture.RevokeAsync(new { medium = "ThisConversation", conversationId = fixture.Conversation, note = "Lo pidió por el chat." }),
            HttpStatusCode.OK);

        var current = body.GetProperty("current");
        Assert.Equal("Revoked", current.GetProperty("event").GetString());
        Assert.Equal("WhatsApp", current.GetProperty("channel").GetString());
        Assert.False(body.GetProperty("canRevoke").GetBoolean());
        var history = body.GetProperty("history");
        Assert.Equal(["Revoked", "Accepted"], history.EnumerateArray().Select(row => row.GetProperty("event").GetString()));
        Assert.Equal("ThisConversation", history[0].GetProperty("medium").GetString());
        Assert.Equal("Lo pidió por el chat.", history[0].GetProperty("note").GetString());
        AssertActor(history[0], SubjectId, MemberName);
        Assert.Equal(
            $"{fixture.Conversation}|WhatsApp|CO.laura1|573001234567|Member|{SubjectId}|ThisConversation|Lo pidió por el chat.",
            await RevocationRowAsync(fixture.ConnectionString));
        Assert.Equal(1L, await RevocationAuditsAsync(fixture.ConnectionString, fixture.CustomerId));
        Assert.Equal("customers.customer.data_consent_revoked", CustomerAuditActions.DataConsentRevoked);
    }

    [Fact]
    public async Task RevokingByAnotherMediumLeavesTheIdentityEmpty()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.RecordBeforeNowAsync(DataConsentEvent.Accepted);

        var body = await BodyAsync(await fixture.RevokeAsync(ByOtherMedium), HttpStatusCode.OK);

        var current = body.GetProperty("current");
        Assert.Equal("Revoked", current.GetProperty("event").GetString());
        Assert.Equal(JsonValueKind.Null, current.GetProperty("channel").ValueKind);
        Assert.Equal("Other", body.GetProperty("history")[0].GetProperty("medium").GetString());
        Assert.Equal(
            $"-|-|-|-|Member|{SubjectId}|Other|Por correo.",
            await RevocationRowAsync(fixture.ConnectionString));
        Assert.Equal(1L, await RevocationAuditsAsync(fixture.ConnectionString, fixture.CustomerId));
    }

    /// <summary>Review Focus 4: sólo una aceptación vigente se revoca. Ni una fila nueva ni una auditoría de más.</summary>
    [Theory]
    [InlineData("none")]
    [InlineData("rejected")]
    [InlineData("revoked")]
    public async Task RevokingWithoutACurrentAcceptanceIs409(string latest)
    {
        await using var fixture = await Fixture.StartAsync();
        switch (latest)
        {
            case "rejected":
                await fixture.RecordBeforeNowAsync(DataConsentEvent.Rejected);
                break;
            case "revoked":
                await fixture.RecordBeforeNowAsync(DataConsentEvent.Accepted);
                await BodyAsync(await fixture.RevokeAsync(ByOtherMedium), HttpStatusCode.OK);
                break;
        }

        var rows = await ConsentRowsAsync(fixture.ConnectionString);
        var audits = await RevocationAuditsAsync(fixture.ConnectionString, fixture.CustomerId);

        var body = await BodyAsync(await fixture.RevokeAsync(ByOtherMedium), HttpStatusCode.Conflict);

        Assert.Equal(NotAccepted, body.GetProperty("code").GetString());
        Assert.Equal(rows, await ConsentRowsAsync(fixture.ConnectionString));
        Assert.Equal(audits, await RevocationAuditsAsync(fixture.ConnectionString, fixture.CustomerId));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("missing")]
    [InlineData("anonymous")]
    public async Task TheConversationMustBeOfThisCustomer(string which)
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.RecordBeforeNowAsync(DataConsentEvent.Accepted);
        var conversation = which switch
        {
            "foreign" => fixture.ForeignConversation,
            "anonymous" => fixture.AnonymousConversation,
            _ => Guid.CreateVersion7(),
        };

        var body = await BodyAsync(
            await fixture.RevokeAsync(new { medium = "ThisConversation", conversationId = conversation, note = "Lo pidió por el chat." }),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal(ConversationInvalid, body.GetProperty("code").GetString());
        Assert.Equal(1L, await ConsentRowsAsync(fixture.ConnectionString));
    }

    [Theory]
    [InlineData("emptyNote", "Note")]
    [InlineData("blankNote", "Note")]
    [InlineData("longNote", "Note")]
    [InlineData("phone", "Medium")]
    [InlineData("noMedium", "Medium")]
    [InlineData("thisConversationWithoutId", "ConversationId")]
    [InlineData("thisConversationEmptyId", "ConversationId")]
    [InlineData("otherWithId", "ConversationId")]
    public async Task TheBodyIsValidatedFieldByField(string @case, string field)
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.RecordBeforeNowAsync(DataConsentEvent.Accepted);
        var body = new Dictionary<string, object?> { ["medium"] = "Other", ["conversationId"] = null, ["note"] = "Por correo." };
        switch (@case)
        {
            case "emptyNote": body["note"] = string.Empty; break;
            case "blankNote": body["note"] = "  "; break;
            case "longNote": body["note"] = new string('a', CustomerDataConsent.NoteMaxLength + 1); break;
            case "phone": body["medium"] = "Phone"; break;
            case "noMedium": body.Remove("medium"); break;
            case "thisConversationWithoutId": body["medium"] = "ThisConversation"; break;
            case "thisConversationEmptyId": body["medium"] = "ThisConversation"; body["conversationId"] = Guid.Empty; break;
            case "otherWithId": body["conversationId"] = fixture.Conversation; break;
        }

        var problem = await BodyAsync(await fixture.RevokeAsync(body), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(error => error.Name));
        Assert.Equal(1L, await ConsentRowsAsync(fixture.ConnectionString));
    }

    [Fact]
    public async Task RevokingNeedsThePermission()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.RecordBeforeNowAsync(DataConsentEvent.Accepted);

        var manageOnly = await fixture.RevokeAsync(ByOtherMedium, fixture.ClientWith(CustomersPermissions.CustomerManage));
        Assert.Equal(HttpStatusCode.Forbidden, manageOnly.StatusCode);
        Assert.Equal(1L, await ConsentRowsAsync(fixture.ConnectionString));

        // D-5: inactivo no impide revocar; la ley no deja de aplicar porque el cliente no compre.
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var deactivate = new NpgsqlCommand(
                $"UPDATE customers.customers SET is_active = false WHERE id = '{fixture.CustomerId}'", connection);
            Assert.Equal(1, await deactivate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        await BodyAsync(await fixture.RevokeAsync(ByOtherMedium), HttpStatusCode.OK);

        // Fusionado gana sobre «sin aceptación vigente»: la última fila ya es la revocación de arriba.
        var survivor = await fixture.CreateOtherCustomerAsync();
        await MarkMergedAsync(fixture.ConnectionString, fixture.CustomerId, survivor.Id);
        var merged = await BodyAsync(await fixture.RevokeAsync(ByOtherMedium), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("customers.customer.merged", merged.GetProperty("code").GetString());

        var missing = await BodyAsync(
            await fixture.Revoker().PostAsJsonAsync(
                $"{fixture.Url(id: Guid.CreateVersion7())}/revocations", ByOtherMedium, TestContext.Current.CancellationToken),
            HttpStatusCode.NotFound);
        Assert.Equal("customers.customer.not_found", missing.GetProperty("code").GetString());
    }

    /// <summary>Carry de la Task 8: quien revocó sale con su nombre, y el que ya no existe, como «Usuario eliminado».</summary>
    [Fact]
    public async Task TheRevokingMemberIsNamedOrShownAsDeleted()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.RecordBeforeNowAsync(DataConsentEvent.Accepted);
        await BodyAsync(await fixture.RevokeAsync(ByOtherMedium), HttpStatusCode.OK);
        // La segunda aceptación va después de la primera revocación, que lleva la hora del servidor.
        await fixture.RecordAsync(DataConsentEvent.Accepted, DateTimeOffset.UtcNow);
        await BodyAsync(await fixture.RevokeAsync(ByOtherMedium, fixture.Revoker(GoneSubjectId)), HttpStatusCode.OK);

        var body = await BodyAsync(
            await fixture.Client.GetAsync(fixture.Url(), TestContext.Current.CancellationToken), HttpStatusCode.OK);

        var history = body.GetProperty("history");
        Assert.Equal(
            ["Revoked", "Accepted", "Revoked", "Accepted"],
            history.EnumerateArray().Select(row => row.GetProperty("event").GetString()));
        AssertActor(history[0], GoneSubjectId, "Usuario eliminado");
        AssertActor(history[2], SubjectId, MemberName);
    }
}
