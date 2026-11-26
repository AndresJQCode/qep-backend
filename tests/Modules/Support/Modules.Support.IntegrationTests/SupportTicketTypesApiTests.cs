using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Support.Application;
using Npgsql;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// La API de tipos de ticket: la siembra que ve el primer acceso, el alta al final de la lista, el nombre único sin distinguir
/// mayúsculas, la edición con <c>If-Match</c>, la desactivación y el orden completo. Comparten host: cada prueba usa su tenant.
/// </summary>
public sealed class SupportTicketTypesApiTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFirstReadReturnsTheEightDefaultTypes()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.Read);

        var response = await client.GetAsync($"{SupportUrl(tenant)}/ticket-types", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listed = (await JsonAsync(response)).EnumerateArray().ToList();
        // Los ocho de siempre y, al final, el tipo interno marcado (spec 2026-11-22, D9).
        Assert.Equal(9, listed.Count);
        Assert.True(listed[^1].GetProperty("isInternal").GetBoolean());
        var types = listed.Take(8).ToList();
        Assert.Equal(
            [
                ("Petición general", (int?)15, true, true),
                ("Petición de documentos o información", 10, true, true),
                ("Consulta", 30, true, true),
                ("Queja", 15, true, true),
                ("Reclamo", 15, true, true),
                ("Sugerencia", 15, true, false),
                ("Felicitación", 15, true, false),
                ("Soporte técnico", null, false, false),
            ],
            types.Select(type => (
                type.GetProperty("name").GetString()!,
                Term(type),
                type.GetProperty("termIsLegal").GetBoolean(),
                type.GetProperty("requiresFormalResponse").GetBoolean())));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], types.Select(type => type.GetProperty("position").GetInt32()));
        Assert.All(types, type =>
        {
            Assert.True(type.GetProperty("isActive").GetBoolean());
            Assert.True(type.GetProperty("isPublished").GetBoolean());
            Assert.Equal(1L, type.GetProperty("version").GetInt64());
        });
    }

    [Fact]
    public async Task CreatingATypePutsItLast()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var response = await client.PostAsJsonAsync($"{SupportUrl(tenant)}/settings/ticket-types", TypeBody("Garantía", 15, true), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await JsonAsync(response);
        Assert.Equal("Garantía", created.GetProperty("name").GetString());
        Assert.Equal(15, created.GetProperty("termBusinessDays").GetInt32());
        Assert.True(created.GetProperty("termIsLegal").GetBoolean());
        // El tipo interno ocupa la 9: el nuevo no empata con ella.
        Assert.Equal(10, created.GetProperty("position").GetInt32());
        Assert.True(created.GetProperty("isActive").GetBoolean());
        Assert.Equal(1L, created.GetProperty("version").GetInt64());
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal($"{SupportUrl(tenant)}/settings/ticket-types/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, SupportAuditActions.TicketTypeCreated, id.ToString()));
        var listed = await JsonAsync(await client.GetAsync($"{SupportUrl(tenant)}/settings/ticket-types", Ct));
        Assert.Equal("Garantía", listed.EnumerateArray().Last().GetProperty("name").GetString());
    }

    [Fact]
    public async Task ATakenNameIs409()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var response = await client.PostAsJsonAsync($"{SupportUrl(tenant)}/settings/ticket-types", TypeBody("queja", 15, true), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("support.ticket_type.name_taken", await CodeOfAsync(response));
    }

    [Fact]
    public async Task LegalWithoutTermIs422OnTheField()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);

        var response = await client.PostAsJsonAsync($"{SupportUrl(tenant)}/settings/ticket-types", TypeBody("Garantía", null, true), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonAsync(response);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.Equal(["TermIsLegal"], problem.GetProperty("errors").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task UpdatingWithoutIfMatchIs428AndWithAnOldVersionIs412()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var queja = await TypeNamedAsync(client, tenant, "Queja");
        var url = $"{SupportUrl(tenant)}/settings/ticket-types/{queja.GetProperty("id").GetGuid()}";

        var withoutVersion = await PutAsync(client, url, UpdateBody("Queja formal", 15, true, true), version: null);
        var renamed = await PutAsync(client, url, UpdateBody("Queja formal", 15, true, true), version: 1);
        var stale = await PutAsync(client, url, UpdateBody("Queja vieja", 15, true, true), version: 1);

        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutVersion.StatusCode);
        Assert.Equal("precondition.if_match_required", await CodeOfAsync(withoutVersion));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var body = await JsonAsync(renamed);
        Assert.Equal("Queja formal", body.GetProperty("name").GetString());
        Assert.Equal(2L, body.GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", await CodeOfAsync(stale));
        Assert.Equal(2L, (await TypeNamedAsync(client, tenant, "Queja formal")).GetProperty("version").GetInt64());
    }

    // Fase 4b (H6): el «siguiente paso» del tipo pisa el del tenant. Nace en null en la siembra; el alta y la edición lo guardan
    // recortado. En la edición, ausente o null lo conserva y "" lo borra, la misma regla que el PUT de ajustes: el frontend publicado
    // no manda el campo y no debe borrarlo (revisión final). Un PUT sin cambios no sube la versión ni audita.
    [Fact]
    public async Task ATypeNextStepRoundTrips()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var queja = await TypeNamedAsync(client, tenant, "Queja");
        var url = $"{SupportUrl(tenant)}/settings/ticket-types/{queja.GetProperty("id").GetGuid()}";

        var created = await client.PostAsJsonAsync(
            $"{SupportUrl(tenant)}/settings/ticket-types",
            new
            {
                name = "Garantía",
                termBusinessDays = 15,
                termIsLegal = true,
                requiresFormalResponse = true,
                isPublished = true,
                formalNextStep = "  Puedes acudir a la SIC.  ",
            },
            Ct);
        var updated = await PutAsync(client, url, NextStepBody("Si no estás de acuerdo, escríbenos."), version: 1);
        var keptWithoutField = await PutAsync(client, url, BodyWithoutNextStep(), version: 2);
        var keptWithNull = await PutAsync(client, url, NextStepBody(null), version: 2);
        var cleared = await PutAsync(client, url, NextStepBody(""), version: 2);

        Assert.Equal(JsonValueKind.Null, queja.GetProperty("formalNextStep").ValueKind);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Puedes acudir a la SIC.", (await JsonAsync(created)).GetProperty("formalNextStep").GetString());
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Si no estás de acuerdo, escríbenos.", (await JsonAsync(updated)).GetProperty("formalNextStep").GetString());
        Assert.Equal(HttpStatusCode.OK, keptWithoutField.StatusCode);
        Assert.Equal("Si no estás de acuerdo, escríbenos.", (await JsonAsync(keptWithoutField)).GetProperty("formalNextStep").GetString());
        Assert.Equal(2, (await JsonAsync(keptWithoutField)).GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.OK, keptWithNull.StatusCode);
        Assert.Equal("Si no estás de acuerdo, escríbenos.", (await JsonAsync(keptWithNull)).GetProperty("formalNextStep").GetString());
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await TypeNamedAsync(client, tenant, "Queja")).GetProperty("formalNextStep").ValueKind);
        Assert.Equal(2, await CountAuditEntriesAsync(factory, SupportAuditActions.TicketTypeUpdated, queja.GetProperty("id").GetGuid().ToString()));
    }

    [Fact]
    public async Task ANextStepOver2000Is422()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var queja = await TypeNamedAsync(client, tenant, "Queja");

        var response = await PutAsync(
            client,
            $"{SupportUrl(tenant)}/settings/ticket-types/{queja.GetProperty("id").GetGuid()}",
            NextStepBody(new string('a', 2001)),
            version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        Assert.Equal(["FormalNextStep"], await ErrorKeysAsync(response));
    }

    private static object BodyWithoutNextStep() => new
    {
        name = "Queja",
        termBusinessDays = 15,
        termIsLegal = true,
        requiresFormalResponse = true,
        isPublished = true,
        isActive = true,
    };

    private static object NextStepBody(string? formalNextStep) => new
    {
        name = "Queja",
        termBusinessDays = 15,
        termIsLegal = true,
        requiresFormalResponse = true,
        isPublished = true,
        isActive = true,
        formalNextStep,
    };

    [Fact]
    public async Task UpdatingATypeOfAnotherTenantIs404()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var (_, otherClient, other) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var foreign = await TypeNamedAsync(otherClient, other, "Queja");

        var response = await PutAsync(
            client,
            $"{SupportUrl(tenant)}/settings/ticket-types/{foreign.GetProperty("id").GetGuid()}",
            UpdateBody("Queja", 15, true, true),
            version: 1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("support.ticket_type.not_found", await CodeOfAsync(response));
    }

    [Fact]
    public async Task DeactivatingKeepsTheTypeListedForSettings()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage, SupportPermissions.Read);
        var sugerencia = await TypeNamedAsync(client, tenant, "Sugerencia");
        var id = sugerencia.GetProperty("id").GetGuid();

        var response = await PutAsync(
            client, $"{SupportUrl(tenant)}/settings/ticket-types/{id}", UpdateBody("Sugerencia", 15, true, false), version: 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await JsonAsync(response)).GetProperty("isActive").GetBoolean());
        var forSettings = await JsonAsync(await client.GetAsync($"{SupportUrl(tenant)}/settings/ticket-types", Ct));
        var forTickets = await JsonAsync(await client.GetAsync($"{SupportUrl(tenant)}/ticket-types", Ct));
        Assert.False(forSettings.EnumerateArray().Single(type => type.GetProperty("id").GetGuid() == id).GetProperty("isActive").GetBoolean());
        Assert.False(forTickets.EnumerateArray().Single(type => type.GetProperty("id").GetGuid() == id).GetProperty("isActive").GetBoolean());
        Assert.Equal(9, forTickets.GetArrayLength());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, SupportAuditActions.TicketTypeUpdated, id.ToString()));
    }

    [Fact]
    public async Task ReorderingNeedsTheCompleteList()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var ids = (await JsonAsync(await client.GetAsync($"{SupportUrl(tenant)}/settings/ticket-types", Ct)))
            .EnumerateArray().Select(type => type.GetProperty("id").GetGuid()).ToList();
        var url = $"{SupportUrl(tenant)}/settings/ticket-types/order";

        var incomplete = await PutAsync(client, url, new { typeIds = ids.Take(7).ToArray() }, version: null);
        var repeated = await PutAsync(client, url, new { typeIds = ids.Take(7).Append(ids[0]).ToArray() }, version: null);
        var reversed = await PutAsync(client, url, new { typeIds = Enumerable.Reverse(ids).ToArray() }, version: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, incomplete.StatusCode);
        Assert.Equal("support.ticket_type.order_incomplete", await CodeOfAsync(incomplete));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, repeated.StatusCode);
        Assert.Equal("support.ticket_type.order_incomplete", await CodeOfAsync(repeated));
        Assert.Equal(HttpStatusCode.OK, reversed.StatusCode);
        var ordered = (await JsonAsync(reversed)).EnumerateArray().ToList();
        Assert.Equal(Enumerable.Reverse(ids), ordered.Select(type => type.GetProperty("id").GetGuid()));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], ordered.Select(type => type.GetProperty("position").GetInt32()));
        Assert.Equal("Soporte técnico", ordered[0].GetProperty("name").GetString());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, SupportAuditActions.TicketTypesReordered, tenant));
    }

    // Revisión final, m5: el alta de un tipo toma la fila de ajustes del tenant (la misma que bloquea el radicado) antes de leer la
    // posición mayor, así dos altas a la vez no comparten posición. Acá la fila la tiene otra transacción: las dos altas esperan, y al
    // soltarla salen una tras otra con 10 y 11 (la 9 es del tipo interno). Host propio: la prueba mide una espera (como SupportCustomerReferenceLockApiTests).
    [Fact]
    public async Task ConcurrentCreationsWaitForTheTenantRowAndGetDistinctPositions()
    {
        var database = await StartDatabaseAsync();
        await using var _d = database;
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = NewTenantId();
        await SeedTenantAsync(factory, tenant);
        using var client = CreateClient(factory, SubjectId, tenant, SupportPermissions.SettingsManage);
        // Calentamiento y provisión: la fila de ajustes tiene que existir antes de bloquearla.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{SupportUrl(tenant)}/settings/ticket-types", Ct)).StatusCode);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand(
            "SELECT 1 FROM support.tenant_settings WHERE tenant_id = @tenant FOR UPDATE", connection, transaction))
        {
            lockRow.Parameters.AddWithValue("tenant", Guid.Parse(tenant));
            await lockRow.ExecuteNonQueryAsync(Ct);
        }

        var first = client.PostAsJsonAsync($"{SupportUrl(tenant)}/settings/ticket-types", TypeBody("Garantía", 15, true), Ct);
        var second = client.PostAsJsonAsync($"{SupportUrl(tenant)}/settings/ticket-types", TypeBody("Devolución", 15, true), Ct);
        var both = Task.WhenAll(first, second);

        Assert.NotSame(both, await Task.WhenAny(both, Task.Delay(TimeSpan.FromMilliseconds(500), Ct)));
        await transaction.RollbackAsync(Ct);
        var responses = await both;

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var positions = new List<int>();
        foreach (var response in responses)
        {
            positions.Add((await JsonAsync(response)).GetProperty("position").GetInt32());
        }

        Assert.Equal([10, 11], positions.Order());
    }

    // Fase 4a (plan G4–G6): borrar un tipo que ningún ticket usa.
    [Fact]
    public async Task DeletingAnUnusedTypeRemovesItAndAudits()
    {
        var (factory, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var created = await JsonAsync(
            await client.PostAsJsonAsync($"{SupportUrl(tenant)}/settings/ticket-types", TypeBody("Garantía", 15, true), Ct));
        var id = created.GetProperty("id").GetGuid();

        var response = await DeleteAsync(client, tenant, id, created.GetProperty("version").GetInt64());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var listed = await JsonAsync(await client.GetAsync($"{SupportUrl(tenant)}/settings/ticket-types", Ct));
        Assert.DoesNotContain(listed.EnumerateArray(), type => type.GetProperty("id").GetGuid() == id);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, SupportAuditActions.TicketTypeDeleted, id.ToString()));
    }

    [Fact]
    public async Task DeletingATypeWithAClosedTicketIs422InUse()
    {
        var scenario = await SupportTicketScenario.StartAsync(await sharedHost.StartTestAsync());
        var ticket = await scenario.CreateAsync("Queja");
        ticket = await scenario.MoveAsync(ticket, "Resolved");
        await scenario.MoveAsync(ticket, "Closed");
        var queja = await TypeNamedAsync(scenario.Client, scenario.Tenant, "Queja");
        var id = queja.GetProperty("id").GetGuid();

        var response = await DeleteAsync(scenario.Client, scenario.Tenant, id, queja.GetProperty("version").GetInt64());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.ticket_type.in_use", await CodeOfAsync(response));
        Assert.Equal(id, (await TypeNamedAsync(scenario.Client, scenario.Tenant, "Queja")).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task DeletingWithoutIfMatchIs428AndWithAnOldVersionIs412()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var sugerencia = await TypeNamedAsync(client, tenant, "Sugerencia");
        var id = sugerencia.GetProperty("id").GetGuid();
        var renamed = await PutAsync(
            client, $"{SupportUrl(tenant)}/settings/ticket-types/{id}", UpdateBody("Sugerencias", 15, true, true), version: 1);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var withoutVersion = await DeleteAsync(client, tenant, id, version: null);
        var stale = await DeleteAsync(client, tenant, id, version: 1);

        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutVersion.StatusCode);
        Assert.Equal("precondition.if_match_required", await CodeOfAsync(withoutVersion));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", await CodeOfAsync(stale));
        Assert.Equal(id, (await TypeNamedAsync(client, tenant, "Sugerencias")).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task DeletingATypeOfAnotherTenantIs404()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var (_, otherClient, other) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var foreign = await TypeNamedAsync(otherClient, other, "Felicitación");

        var response = await DeleteAsync(client, tenant, foreign.GetProperty("id").GetGuid(), version: 1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("support.ticket_type.not_found", await CodeOfAsync(response));
        Assert.Equal(8, (await JsonAsync(await otherClient.GetAsync($"{SupportUrl(other)}/settings/ticket-types", Ct))).GetArrayLength());
    }

    [Fact]
    public async Task AReaderCannotDelete()
    {
        var (factory, manager, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var felicitacion = await TypeNamedAsync(manager, tenant, "Felicitación");
        using var reader = CreateClient(factory, SubjectId, tenant, SupportPermissions.Read);

        var response = await DeleteAsync(reader, tenant, felicitacion.GetProperty("id").GetGuid(), version: 1);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheSettingsListSaysWhichTypesAreInUse()
    {
        var scenario = await SupportTicketScenario.StartAsync(await sharedHost.StartTestAsync());
        await scenario.CreateAsync("Queja");

        var listed = (await JsonAsync(await scenario.Client.GetAsync($"{scenario.Url}/settings/ticket-types", Ct))).EnumerateArray().ToList();

        Assert.Equal(8, listed.Count);
        Assert.All(listed, type => Assert.Equal(
            type.GetProperty("name").GetString() == "Queja", type.GetProperty("inUse").GetBoolean()));
        var forTickets = (await JsonAsync(await scenario.Client.GetAsync($"{scenario.Url}/ticket-types", Ct))).EnumerateArray();
        Assert.True(forTickets.Single(type => type.GetProperty("name").GetString() == "Queja").GetProperty("inUse").GetBoolean());
    }

    [Fact]
    public async Task ADeletedSeedTypeIsNotSeededAgain()
    {
        var (_, client, tenant) = await NewTenantAsync(SupportPermissions.SettingsManage);
        var felicitacion = await TypeNamedAsync(client, tenant, "Felicitación");

        var response = await DeleteAsync(client, tenant, felicitacion.GetProperty("id").GetGuid(), version: 1);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var listed = (await JsonAsync(await client.GetAsync($"{SupportUrl(tenant)}/settings/ticket-types", Ct))).EnumerateArray().ToList();
        Assert.Equal(7, listed.Count);
        Assert.DoesNotContain(listed, type => type.GetProperty("name").GetString() == "Felicitación");
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, string tenant, Guid typeId, long? version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{SupportUrl(tenant)}/settings/ticket-types/{typeId}");
        if (version is { } value)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{value}\"");
        }

        return await client.SendAsync(request, Ct);
    }

    private static int? Term(JsonElement type) =>
        type.GetProperty("termBusinessDays").ValueKind == JsonValueKind.Null ? null : type.GetProperty("termBusinessDays").GetInt32();

    private static object TypeBody(string name, int? term, bool legal) => new
    {
        name,
        termBusinessDays = term,
        termIsLegal = legal,
        requiresFormalResponse = true,
        isPublished = true,
    };

    private static object UpdateBody(string name, int? term, bool legal, bool active) => new
    {
        name,
        termBusinessDays = term,
        termIsLegal = legal,
        requiresFormalResponse = false,
        isPublished = true,
        isActive = active,
    };

    private static async Task<JsonElement> TypeNamedAsync(HttpClient client, string tenant, string name) =>
        (await JsonAsync(await client.GetAsync($"{SupportUrl(tenant)}/settings/ticket-types", Ct)))
        .EnumerateArray()
        .Single(type => type.GetProperty("name").GetString() == name);

    private async Task<(QepApiFactory Factory, HttpClient Client, string Tenant)> NewTenantAsync(params string[] permissions)
    {
        var factory = await sharedHost.StartTestAsync();
        var tenant = NewTenantId();
        await SeedTenantAsync(factory, tenant);
        return (factory, CreateClient(factory, SubjectId, tenant, permissions), tenant);
    }
}
