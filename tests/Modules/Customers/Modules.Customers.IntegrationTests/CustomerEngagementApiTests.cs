using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Customers.Application;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Customers.IntegrationTests.CustomersApiHarness;

namespace Modules.Customers.IntegrationTests;

/// <summary>Spec 2026-10-11 §4.2: notas, etiquetas y consentimiento de marketing por HTTP, con el aislamiento de tenant
/// (403, nunca 404), el 409 del índice <c>IX_tags_tenant_name</c> y la auditoría en la misma transacción.</summary>
public sealed class CustomerEngagementApiTests
{
    private const string SecondSubject = "01900000-0000-7000-8000-0000000000c3";

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _database;
        private readonly QepApiFactory _factory;

        private Fixture(PostgreSqlContainer database, QepApiFactory factory, Guid customerId, string connectionString)
        {
            _database = database;
            _factory = factory;
            CustomerId = customerId;
            ConnectionString = connectionString;
        }

        public Guid CustomerId { get; }

        public string ConnectionString { get; }

        /// <summary>Gestiona clientes (crea notas y etiquetas, asigna, apaga marketing).</summary>
        public HttpClient Manager => Client(SubjectId, TenantId, CustomersPermissions.CustomerRead, CustomersPermissions.CustomerManage);

        /// <summary>Otra persona del mismo tenant con los mismos permisos que <see cref="Manager"/>.</summary>
        public HttpClient SecondManager => Client(SecondSubject, TenantId, CustomersPermissions.CustomerRead, CustomersPermissions.CustomerManage);

        public HttpClient Admin => Client(
            SecondSubject,
            TenantId,
            CustomersPermissions.CustomerRead,
            CustomersPermissions.CustomerManage,
            CustomersPermissions.TagManage,
            CustomersPermissions.NoteModerate);

        public HttpClient ReadOnly => Client(SubjectId, TenantId, CustomersPermissions.CustomerRead);

        /// <summary>Con todos los permisos, pero de otro tenant.</summary>
        public HttpClient Stranger => Client(
            OtherSubjectId,
            OtherTenantId,
            CustomersPermissions.CustomerRead,
            CustomersPermissions.CustomerManage,
            CustomersPermissions.TagManage,
            CustomersPermissions.NoteModerate);

        public static async Task<Fixture> StartAsync()
        {
            var database = await StartDatabaseAsync();
            var factory = new QepApiFactory(database.GetConnectionString());
            using var setup = CreateManager(factory);
            var city = await EnsureCityAsync(setup);
            var classification = await CreateClassificationAsync(setup);
            var customer = await CreateCustomerAsync(setup, city.CityId, classification.Id);

            return new Fixture(database, factory, customer.Id, database.GetConnectionString());
        }

        public HttpClient Client(string subject, string tenant, params string[] permissions) =>
            CreateClient(_factory, subject, tenant, permissions);

        public async ValueTask DisposeAsync()
        {
            _factory.Dispose();
            await _database.DisposeAsync();
        }
    }

    private static string Notes(Guid customerId, string tenant = TenantId) => $"{CustomersUrl(tenant)}/{customerId}/notes";

    private static string TagsUrl(string tenant = TenantId) => $"{CustomersUrl(tenant)}/tags";

    private static string CustomerTagsUrl(Guid customerId, string tenant = TenantId) => $"{CustomersUrl(tenant)}/{customerId}/tags";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> PostNoteAsync(HttpClient client, Guid customerId, string body)
    {
        var response = await client.PostAsJsonAsync(Notes(customerId), new { body }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private static async Task<JsonElement> PostTagAsync(HttpClient client, string name, string color = "blue", string tenant = TenantId)
    {
        var response = await client.PostAsJsonAsync(TagsUrl(tenant), new { name, color }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private static async Task<HttpResponseMessage> PutTagsAsync(HttpClient client, Guid customerId, IEnumerable<Guid> tagIds, string tenant = TenantId) =>
        await client.PutAsJsonAsync(CustomerTagsUrl(customerId, tenant), new { tagIds }, TestContext.Current.CancellationToken);

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("code").GetString()!;

    private static async Task<int> CountAuditAsync(string connectionString, string action)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM platform.outbox_messages
            WHERE event_name = 'platform.audit.recorded.v1' AND payload->>'action' = @action
            """,
            connection);
        command.Parameters.AddWithValue("action", action);
        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---- Notas -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANoteIsCreatedListedNewestFirstAndAudited()
    {
        await using var fixture = await Fixture.StartAsync();

        var first = await PostNoteAsync(fixture.Manager, fixture.CustomerId, "Primera");
        await Task.Delay(10, TestContext.Current.CancellationToken);
        var second = await PostNoteAsync(fixture.SecondManager, fixture.CustomerId, "  Segunda  ");

        Assert.Equal("Primera", first.GetProperty("body").GetString());
        Assert.Equal(1, first.GetProperty("version").GetInt64());
        Assert.True(first.GetProperty("canEdit").GetBoolean());
        Assert.True(first.GetProperty("canDelete").GetBoolean());
        Assert.Equal(SubjectId, first.GetProperty("author").GetProperty("userId").GetString());
        // Los sujetos del stub no tienen membresía ni usuario: la pantalla recibe el texto de respaldo, nunca null.
        Assert.Equal("Usuario eliminado", first.GetProperty("author").GetProperty("displayName").GetString());

        var response = await fixture.Manager.GetAsync(Notes(fixture.CustomerId), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await ReadAsync(response);
        Assert.Equal(["Segunda", "Primera"], list.EnumerateArray().Select(note => note.GetProperty("body").GetString()));
        // La de otra persona no se edita ni se borra sin moderación; la propia sí.
        Assert.False(list[0].GetProperty("canEdit").GetBoolean());
        Assert.False(list[0].GetProperty("canDelete").GetBoolean());
        Assert.True(list[1].GetProperty("canEdit").GetBoolean());
        Assert.Equal(second.GetProperty("id").GetString(), list[0].GetProperty("id").GetString());
        Assert.Equal(2, await CountAuditAsync(fixture.ConnectionString, "customers.note.created"));
    }

    [Fact]
    public async Task NotesDoNotBumpTheCustomerVersion()
    {
        await using var fixture = await Fixture.StartAsync();
        var before = await GetAsync(fixture.Manager, fixture.CustomerId);

        await PostNoteAsync(fixture.Manager, fixture.CustomerId, "Una nota");

        var after = await GetAsync(fixture.Manager, fixture.CustomerId);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task OnlyTheAuthorEditsAndAStaleVersionIs412()
    {
        await using var fixture = await Fixture.StartAsync();
        var note = await PostNoteAsync(fixture.Manager, fixture.CustomerId, "Original");
        var url = $"{Notes(fixture.CustomerId)}/{note.GetProperty("id").GetString()}";

        var foreign = await fixture.Admin.PutAsJsonAsync(url, new { body = "Ajena", expectedVersion = 1 }, TestContext.Current.CancellationToken);
        var stale = await fixture.Manager.PutAsJsonAsync(url, new { body = "Vieja", expectedVersion = 5 }, TestContext.Current.CancellationToken);
        var edited = await fixture.Manager.PutAsJsonAsync(url, new { body = "Editada", expectedVersion = 1 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal("customers.note.not_author", await ErrorCodeAsync(foreign));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var body = await ReadAsync(edited);
        Assert.Equal("Editada", body.GetProperty("body").GetString());
        Assert.Equal(2, body.GetProperty("version").GetInt64());
        Assert.Equal(1, await CountAuditAsync(fixture.ConnectionString, "customers.note.updated"));
    }

    [Fact]
    public async Task ABlankNoteIsA422WithTheFieldMap()
    {
        await using var fixture = await Fixture.StartAsync();

        var response = await fixture.Manager.PostAsJsonAsync(Notes(fixture.CustomerId), new { body = "   " }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(["Body"], await ValidationFieldsAsync(response));
    }

    [Fact]
    public async Task DeletingANoteIsForTheAuthorOrAModeratorOnly()
    {
        await using var fixture = await Fixture.StartAsync();
        var mine = await PostNoteAsync(fixture.Manager, fixture.CustomerId, "Mía");
        var theirs = await PostNoteAsync(fixture.SecondManager, fixture.CustomerId, "Suya");
        var mineUrl = $"{Notes(fixture.CustomerId)}/{mine.GetProperty("id").GetString()}";
        var theirsUrl = $"{Notes(fixture.CustomerId)}/{theirs.GetProperty("id").GetString()}";

        var notYours = await fixture.Manager.DeleteAsync(theirsUrl, TestContext.Current.CancellationToken);
        var readOnly = await fixture.ReadOnly.DeleteAsync(mineUrl, TestContext.Current.CancellationToken);
        var own = await fixture.Manager.DeleteAsync(mineUrl, TestContext.Current.CancellationToken);
        var moderated = await fixture.Admin.DeleteAsync(theirsUrl, TestContext.Current.CancellationToken);
        var gone = await fixture.Admin.DeleteAsync(theirsUrl, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, notYours.StatusCode);
        Assert.Equal("customers.note.not_author", await ErrorCodeAsync(notYours));
        Assert.Equal(HttpStatusCode.Forbidden, readOnly.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, moderated.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("customers.note.not_found", await ErrorCodeAsync(gone));
        Assert.Equal(2, await CountAuditAsync(fixture.ConnectionString, "customers.note.deleted"));
    }

    [Fact]
    public async Task NotesOfAnotherTenantAreForbiddenNeverNotFound()
    {
        await using var fixture = await Fixture.StartAsync();
        var note = await PostNoteAsync(fixture.Manager, fixture.CustomerId, "Privada");
        var noteUrl = $"{Notes(fixture.CustomerId)}/{note.GetProperty("id").GetString()}";
        using var stranger = fixture.Stranger;

        var list = await stranger.GetAsync(Notes(fixture.CustomerId), TestContext.Current.CancellationToken);
        var create = await stranger.PostAsJsonAsync(Notes(fixture.CustomerId), new { body = "x" }, TestContext.Current.CancellationToken);
        var edit = await stranger.PutAsJsonAsync(noteUrl, new { body = "x", expectedVersion = 1 }, TestContext.Current.CancellationToken);
        var delete = await stranger.DeleteAsync(noteUrl, TestContext.Current.CancellationToken);

        // La ruta lleva el tenant de la víctima: el llamador es de otro tenant, así que es 403 en las cuatro.
        var asVictimRoute = new[] { list, create, edit, delete };
        Assert.All(asVictimRoute, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        var stillThere = await fixture.Manager.GetAsync(Notes(fixture.CustomerId), TestContext.Current.CancellationToken);
        Assert.Single((await ReadAsync(stillThere)).EnumerateArray());
    }

    // ---- Etiquetas ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATagIsCreatedByAManagerAndListedByNameWithItsCount()
    {
        await using var fixture = await Fixture.StartAsync();

        var vip = await PostTagAsync(fixture.Manager, "VIP", "purple");
        await PostTagAsync(fixture.Manager, "Alfa", "green");
        await PutTagsAsync(fixture.Manager, fixture.CustomerId, [vip.GetProperty("id").GetGuid()]);

        var response = await fixture.ReadOnly.GetAsync(TagsUrl(), TestContext.Current.CancellationToken);
        var list = await ReadAsync(response);
        Assert.Equal(["Alfa", "VIP"], list.EnumerateArray().Select(tag => tag.GetProperty("name").GetString()));
        Assert.Equal([0, 1], list.EnumerateArray().Select(tag => tag.GetProperty("customerCount").GetInt32()));
        Assert.Equal("purple", list[1].GetProperty("color").GetString());
        Assert.Equal(1, list[1].GetProperty("version").GetInt64());
        Assert.Equal(2, await CountAuditAsync(fixture.ConnectionString, "customers.tag.created"));
    }

    [Theory]
    [InlineData("VIP", "VIP")]
    [InlineData("VIP", "vip")]
    [InlineData("VIP", "  Vip ")]
    public async Task ARepeatedNameIsA409TranslatedFromTheIndexRegardlessOfCase(string first, string second)
    {
        await using var fixture = await Fixture.StartAsync();
        await PostTagAsync(fixture.Manager, first);

        var response = await fixture.SecondManager.PostAsJsonAsync(TagsUrl(), new { name = second, color = "red" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("customers.tag.name_taken", await ErrorCodeAsync(response));
        Assert.Single((await ReadAsync(await fixture.Manager.GetAsync(TagsUrl(), TestContext.Current.CancellationToken))).EnumerateArray());
    }

    [Fact]
    public async Task TheSameNameInAnotherTenantIsFine()
    {
        await using var fixture = await Fixture.StartAsync();
        using var stranger = fixture.Stranger;
        await PostTagAsync(fixture.Manager, "VIP");

        var other = await PostTagAsync(stranger, "VIP", "blue", OtherTenantId);

        Assert.Equal("VIP", other.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("", "red", "Name")]
    [InlineData("VIP", "magenta", "Color")]
    public async Task BadTagTextIsA422WithTheFieldMap(string name, string color, string field)
    {
        await using var fixture = await Fixture.StartAsync();

        var response = await fixture.Manager.PostAsJsonAsync(TagsUrl(), new { name, color }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal([field], await ValidationFieldsAsync(response));
    }

    [Fact]
    public async Task RenamingAndDeletingNeedTheTagManagePermissionAndStaleVersionsAre412()
    {
        await using var fixture = await Fixture.StartAsync();
        var tag = await PostTagAsync(fixture.Manager, "VIP");
        var url = $"{TagsUrl()}/{tag.GetProperty("id").GetString()}";

        var forbiddenRename = await fixture.Manager.PutAsJsonAsync(url, new { name = "Otro", color = "red", expectedVersion = 1 }, TestContext.Current.CancellationToken);
        var forbiddenDelete = await fixture.Manager.DeleteAsync(url, TestContext.Current.CancellationToken);
        var stale = await fixture.Admin.PutAsJsonAsync(url, new { name = "Otro", color = "red", expectedVersion = 9 }, TestContext.Current.CancellationToken);
        var renamed = await fixture.Admin.PutAsJsonAsync(url, new { name = "Clientes VIP", color = "orange", expectedVersion = 1 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRename.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenDelete.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var body = await ReadAsync(renamed);
        Assert.Equal("Clientes VIP", body.GetProperty("name").GetString());
        Assert.Equal("orange", body.GetProperty("color").GetString());
        Assert.Equal(2, body.GetProperty("version").GetInt64());
        Assert.Equal(1, await CountAuditAsync(fixture.ConnectionString, "customers.tag.updated"));
    }

    [Fact]
    public async Task RenamingToAnotherTagsNameIsA409()
    {
        await using var fixture = await Fixture.StartAsync();
        await PostTagAsync(fixture.Manager, "VIP");
        var other = await PostTagAsync(fixture.Manager, "Mayorista");

        var response = await fixture.Admin.PutAsJsonAsync(
            $"{TagsUrl()}/{other.GetProperty("id").GetString()}", new { name = "vip", color = "red", expectedVersion = 1 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("customers.tag.name_taken", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task DeletingATagTakesItsAssignmentsWithIt()
    {
        await using var fixture = await Fixture.StartAsync();
        var tag = await PostTagAsync(fixture.Manager, "VIP");
        await PutTagsAsync(fixture.Manager, fixture.CustomerId, [tag.GetProperty("id").GetGuid()]);

        var delete = await fixture.Admin.DeleteAsync($"{TagsUrl()}/{tag.GetProperty("id").GetString()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var customerTags = await ReadAsync(await fixture.Manager.GetAsync(CustomerTagsUrl(fixture.CustomerId), TestContext.Current.CancellationToken));
        Assert.Empty(customerTags.EnumerateArray());
        Assert.Equal(1, await CountAuditAsync(fixture.ConnectionString, "customers.tag.deleted"));
    }

    [Fact]
    public async Task TagsOfAnotherTenantAreForbiddenNeverNotFound()
    {
        await using var fixture = await Fixture.StartAsync();
        var tag = await PostTagAsync(fixture.Manager, "VIP");
        var url = $"{TagsUrl()}/{tag.GetProperty("id").GetString()}";
        using var stranger = fixture.Stranger;

        var responses = new[]
        {
            await stranger.GetAsync(TagsUrl(), TestContext.Current.CancellationToken),
            await stranger.PostAsJsonAsync(TagsUrl(), new { name = "x", color = "red" }, TestContext.Current.CancellationToken),
            await stranger.PutAsJsonAsync(url, new { name = "x", color = "red", expectedVersion = 1 }, TestContext.Current.CancellationToken),
            await stranger.DeleteAsync(url, TestContext.Current.CancellationToken),
            await stranger.GetAsync(CustomerTagsUrl(fixture.CustomerId), TestContext.Current.CancellationToken),
            await PutTagsAsync(stranger, fixture.CustomerId, []),
            await stranger.PutAsJsonAsync($"{CustomersUrl()}/{fixture.CustomerId}/marketing-consent", new { optedOut = true }, TestContext.Current.CancellationToken),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
    }

    // ---- Etiquetas de un cliente -------------------------------------------------------------------------------

    [Fact]
    public async Task TheCustomerTagSetIsReplacedAndReturnedWholeAndInOrder()
    {
        await using var fixture = await Fixture.StartAsync();
        var zeta = (await PostTagAsync(fixture.Manager, "Zeta")).GetProperty("id").GetGuid();
        var alfa = (await PostTagAsync(fixture.Manager, "alfa")).GetProperty("id").GetGuid();
        var beta = (await PostTagAsync(fixture.Manager, "Beta")).GetProperty("id").GetGuid();

        var first = await PutTagsAsync(fixture.Manager, fixture.CustomerId, [zeta, alfa]);
        var second = await PutTagsAsync(fixture.Manager, fixture.CustomerId, [beta, zeta]);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(["alfa", "Zeta"], (await ReadAsync(first)).EnumerateArray().Select(tag => tag.GetProperty("name").GetString()));
        Assert.Equal(["Beta", "Zeta"], (await ReadAsync(second)).EnumerateArray().Select(tag => tag.GetProperty("name").GetString()));
        var customer = await ReadAsync(await fixture.Manager.GetAsync($"{CustomersUrl()}/{fixture.CustomerId}", TestContext.Current.CancellationToken));
        Assert.Equal(["Beta", "Zeta"], customer.GetProperty("tags").EnumerateArray().Select(tag => tag.GetProperty("name").GetString()));
        Assert.Equal(2, await CountAuditAsync(fixture.ConnectionString, "customers.customer.tags_changed"));
    }

    [Fact]
    public async Task SettingTheSameTagsAgainChangesNothingAndLeavesNoAudit()
    {
        await using var fixture = await Fixture.StartAsync();
        var tag = (await PostTagAsync(fixture.Manager, "VIP")).GetProperty("id").GetGuid();
        await PutTagsAsync(fixture.Manager, fixture.CustomerId, [tag]);

        var again = await PutTagsAsync(fixture.Manager, fixture.CustomerId, [tag]);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await CountAuditAsync(fixture.ConnectionString, "customers.customer.tags_changed"));
    }

    [Fact]
    public async Task MoreThanTwentyOrRepeatedTagsIsA422OnTagIds()
    {
        await using var fixture = await Fixture.StartAsync();
        var tag = (await PostTagAsync(fixture.Manager, "VIP")).GetProperty("id").GetGuid();

        var tooMany = await PutTagsAsync(fixture.Manager, fixture.CustomerId, Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()));
        var repeated = await PutTagsAsync(fixture.Manager, fixture.CustomerId, [tag, tag]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
        Assert.Equal(["TagIds"], await ValidationFieldsAsync(tooMany));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, repeated.StatusCode);
        Assert.Equal(["TagIds"], await ValidationFieldsAsync(repeated));
    }

    [Fact]
    public async Task AnUnknownTagIs404AndATagOfAnotherTenantToo()
    {
        await using var fixture = await Fixture.StartAsync();
        using var stranger = fixture.Stranger;
        var foreign = (await PostTagAsync(stranger, "Ajena", "red", OtherTenantId)).GetProperty("id").GetGuid();

        var unknown = await PutTagsAsync(fixture.Manager, fixture.CustomerId, [Guid.NewGuid()]);
        var crossTenant = await PutTagsAsync(fixture.Manager, fixture.CustomerId, [foreign]);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("customers.tag.not_found", await ErrorCodeAsync(unknown));
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.Equal("customers.tag.not_found", await ErrorCodeAsync(crossTenant));
    }

    [Fact]
    public async Task TheHundredAndFirstTagOfATenantIsA422()
    {
        await using var fixture = await Fixture.StartAsync();
        for (var index = 0; index < 100; index++)
        {
            await PostTagAsync(fixture.Manager, $"Etiqueta {index}");
        }

        var response = await fixture.Manager.PostAsJsonAsync(TagsUrl(), new { name = "Una más", color = "red" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("customers.tag.limit_reached", await ErrorCodeAsync(response));
    }

    // ---- Consentimiento de marketing ---------------------------------------------------------------------------

    [Fact]
    public async Task ACustomerStartsAcceptingMarketingAndThePutTogglesItWithAudit()
    {
        await using var fixture = await Fixture.StartAsync();
        var url = $"{CustomersUrl()}/{fixture.CustomerId}/marketing-consent";
        var initial = await ReadAsync(await fixture.Manager.GetAsync($"{CustomersUrl()}/{fixture.CustomerId}", TestContext.Current.CancellationToken));

        var off = await fixture.Manager.PutAsJsonAsync(url, new { optedOut = true }, TestContext.Current.CancellationToken);
        var detail = await ReadAsync(await fixture.Manager.GetAsync($"{CustomersUrl()}/{fixture.CustomerId}", TestContext.Current.CancellationToken));
        var on = await fixture.Manager.PutAsJsonAsync(url, new { optedOut = false }, TestContext.Current.CancellationToken);

        Assert.False(initial.GetProperty("marketingConsent").GetProperty("optedOut").GetBoolean());
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("marketingConsent").GetProperty("optedOutAt").ValueKind);
        Assert.Empty(initial.GetProperty("tags").EnumerateArray());
        var offBody = await ReadAsync(off);
        Assert.True(offBody.GetProperty("optedOut").GetBoolean());
        Assert.Equal("manual", offBody.GetProperty("source").GetString());
        Assert.NotEqual(JsonValueKind.Null, offBody.GetProperty("optedOutAt").ValueKind);
        Assert.True(detail.GetProperty("marketingConsent").GetProperty("optedOut").GetBoolean());
        var onBody = await ReadAsync(on);
        Assert.False(onBody.GetProperty("optedOut").GetBoolean());
        Assert.Equal(JsonValueKind.Null, onBody.GetProperty("source").ValueKind);
        Assert.Equal(2, await CountAuditAsync(fixture.ConnectionString, "customers.customer.marketing_consent_changed"));
    }

    [Fact]
    public async Task ReadOnlyUsersCannotChangeTheConsentOrTheTags()
    {
        await using var fixture = await Fixture.StartAsync();

        var consent = await fixture.ReadOnly.PutAsJsonAsync(
            $"{CustomersUrl()}/{fixture.CustomerId}/marketing-consent", new { optedOut = true }, TestContext.Current.CancellationToken);
        var tags = await PutTagsAsync(fixture.ReadOnly, fixture.CustomerId, []);
        var createTag = await fixture.ReadOnly.PostAsJsonAsync(TagsUrl(), new { name = "x", color = "red" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, consent.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, tags.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, createTag.StatusCode);
    }
}
