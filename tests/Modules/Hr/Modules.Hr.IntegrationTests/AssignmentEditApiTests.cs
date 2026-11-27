using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Application;
using Npgsql;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Editar y eliminar registros de «Cargo y lugar de trabajo» por HTTP (spec 2026-11-26). El alta es la de
/// <see cref="HrApiSeeds.HireBody"/>: inicio 2026-01-15, fin 2027-01-14; el reloj, 2026-11-20 10:00 en Bogotá.
/// </summary>
public sealed class AssignmentEditApiTests : IClassFixture<HrApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public AssignmentEditApiTests(HrApiFixture fixture) => _fixture = fixture;

    private static string ContractUrl(HrTenant tenant, JsonElement detail) =>
        $"{EmployeeUrl(tenant, detail)}/contracts/{detail.GetProperty("contract").GetProperty("id").GetGuid()}";

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {response.StatusCode}: {await BodyAsync(response)}");
        return await JsonOfAsync(response);
    }

    private static Task<HttpResponseMessage> AddAssignmentAsync(
        HrTenant tenant, JsonElement detail, string effectiveDate, Guid positionId, Guid areaId, Guid workplaceId) =>
        tenant.Hr.PostAsJsonAsync(
            $"{ContractUrl(tenant, detail)}/assignments", new { effectiveDate, positionId, areaId, workplaceId }, Ct);

    private static JsonElement Assignments(JsonElement detail) => detail.GetProperty("contract").GetProperty("assignments");

    private static (bool CanEdit, bool CanDelete) FlagsOf(JsonElement record) =>
        (record.GetProperty("canEdit").GetBoolean(), record.GetProperty("canDelete").GetBoolean());

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.True(response.StatusCode == status, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static long VersionOf(JsonElement detail) => detail.GetProperty("employee").GetProperty("version").GetInt64();

    private static string ContractIdOf(JsonElement detail) => detail.GetProperty("contract").GetProperty("id").GetGuid().ToString();

    /// <summary>El registro de esa fecha en la respuesta.</summary>
    private static JsonElement RecordAt(JsonElement detail, string effectiveDate) =>
        Assignments(detail).EnumerateArray().Single(record => record.GetProperty("effectiveDate").GetString() == effectiveDate);

    private static Guid IdAt(JsonElement detail, string effectiveDate) => RecordAt(detail, effectiveDate).GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> RewriteAsync(
        HrTenant tenant,
        JsonElement detail,
        Guid recordId,
        string effectiveDate,
        Guid positionId,
        Guid? areaId,
        Guid workplaceId,
        bool? confirm = true,
        string? ifMatch = "")
    {
        var body = new { effectiveDate, positionId, areaId, workplaceId, confirmRewrite = confirm };
        return PutAsync(tenant.Hr, $"{ContractUrl(tenant, detail)}/assignments/{recordId}", body, ifMatch == "" ? Version(detail) : ifMatch);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HrTenant tenant, JsonElement detail, Guid recordId, string? ifMatch = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{ContractUrl(tenant, detail)}/assignments/{recordId}");
        if ((ifMatch == "" ? Version(detail) : ifMatch) is { } value)
        {
            request.Headers.TryAddWithoutValidation("If-Match", value);
        }

        return await tenant.Hr.SendAsync(request, Ct);
    }

    private static async Task<Guid> PositionAsync(HrTenant tenant, string name) =>
        (await CreatedAsync(tenant.Hr, $"{tenant.Url}/positions", new { name })).GetProperty("id").GetGuid();

    /// <summary>
    /// Alta (2026-01-15, cargo de la semilla) más un registro desde el 2026-03-10 con el cargo «Coordinador».
    /// </summary>
    private async Task<(HrTenant Tenant, SeededSetup Setup, JsonElement Detail, Guid Coordinator)> SceneAsync()
    {
        var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var hired = await HireAsync(tenant, setup);
        var coordinator = await PositionAsync(tenant, "Coordinador");
        var detail = await OkAsync(await AddAssignmentAsync(tenant, hired, "2026-03-10", coordinator, setup.AreaId, setup.WorkplaceId));
        return (tenant, setup, detail, coordinator);
    }

    private async Task<List<string>> ReleaseEventsAsync(HrTenant tenant)
    {
        var connectionString = _fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT payload::text FROM platform.outbox_messages
            WHERE event_name = 'hr.employee.documents-released.v1' AND payload->>'tenantId' = @tenant
            """,
            connection);
        command.Parameters.AddWithValue("tenant", tenant.Id.ToString());
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    [Fact]
    public async Task TheDetailSaysWhichRecordsCanBeEditedOrDeleted()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var coordinator = (await CreatedAsync(tenant.Hr, $"{tenant.Url}/positions", new { name = "Coordinador" })).GetProperty("id").GetGuid();
        await OkAsync(await AddAssignmentAsync(tenant, detail, "2026-03-10", coordinator, setup.AreaId, setup.WorkplaceId));
        var shown = await OkAsync(await AddAssignmentAsync(tenant, detail, "2026-05-20", setup.PositionId, setup.AreaId, setup.WorkplaceId));

        var records = Assignments(shown).EnumerateArray().ToList();
        Assert.Equal(3, records.Count);
        Assert.Equal((true, true), FlagsOf(records[0]));
        // Quitar el del medio dejaría iguales al primero y al último.
        Assert.Equal((true, false), FlagsOf(records[1]));
        Assert.Equal((true, false), FlagsOf(records[2]));

        await OkAsync(await tenant.Hr.PostAsJsonAsync(
            $"{ContractUrl(tenant, detail)}/terminate", new { reason = "Resignation", date = "2026-11-30", notes = (string?)null }, Ct));
        var terminated = await GetEmployeeAsync(tenant, detail);

        var previous = Assert.Single(terminated.GetProperty("previousContracts").EnumerateArray());
        Assert.All(previous.GetProperty("assignments").EnumerateArray(), record => Assert.Equal((false, false), FlagsOf(record)));
    }

    [Fact]
    public async Task ARewriteChangesTheRecordAndLeavesATrace()
    {
        var scene = await SceneAsync();
        using var tenant = scene.Tenant;
        var supervisor = await PositionAsync(tenant, "Supervisor");

        var rewritten = await OkAsync(await RewriteAsync(
            tenant, scene.Detail, IdAt(scene.Detail, "2026-03-10"), "2026-04-01", supervisor, scene.Setup.AreaId, scene.Setup.WorkplaceId));

        var record = RecordAt(rewritten, "2026-04-01");
        Assert.Equal("Supervisor", record.GetProperty("position").GetProperty("name").GetString());
        Assert.Equal(2, Assignments(rewritten).GetArrayLength());
        Assert.Equal(VersionOf(scene.Detail) + 1, VersionOf(rewritten));
        Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, "hr.contract.assignment_rewritten", ContractIdOf(scene.Detail)));
        var activity = await JsonOfAsync(await tenant.Hr.GetAsync($"{EmployeeUrl(tenant, scene.Detail)}/activity", Ct));
        Assert.Equal("AssignmentRewritten", activity.GetProperty("items")[0].GetProperty("action").GetString());
    }

    [Fact]
    public async Task ARewriteWithoutConfirmationIs422RewriteUnconfirmed()
    {
        var scene = await SceneAsync();
        using var tenant = scene.Tenant;

        var problem = await ProblemAsync(
            await RewriteAsync(
                tenant, scene.Detail, IdAt(scene.Detail, "2026-03-10"), "2026-03-10", scene.Setup.PositionId, null, scene.Setup.WorkplaceId, confirm: null),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.assignment.rewrite_unconfirmed", CodeOf(problem));
    }

    [Fact]
    public async Task TheTwoRoutesNeedIfMatch()
    {
        var scene = await SceneAsync();
        using var tenant = scene.Tenant;
        var id = IdAt(scene.Detail, "2026-03-10");
        var supervisor = await PositionAsync(tenant, "Supervisor");

        var putMissing = await ProblemAsync(
            await RewriteAsync(tenant, scene.Detail, id, "2026-03-10", supervisor, null, scene.Setup.WorkplaceId, ifMatch: null),
            HttpStatusCode.PreconditionRequired);
        var putStale = await ProblemAsync(
            await RewriteAsync(tenant, scene.Detail, id, "2026-03-10", supervisor, null, scene.Setup.WorkplaceId, ifMatch: "\"99\""),
            HttpStatusCode.PreconditionFailed);
        var deleteMissing = await ProblemAsync(await DeleteAsync(tenant, scene.Detail, id, ifMatch: null), HttpStatusCode.PreconditionRequired);
        var deleteStale = await ProblemAsync(await DeleteAsync(tenant, scene.Detail, id, ifMatch: "\"99\""), HttpStatusCode.PreconditionFailed);

        Assert.Equal("precondition.if_match_required", CodeOf(putMissing));
        Assert.Equal("precondition.if_match_required", CodeOf(deleteMissing));
        Assert.Equal("concurrency.conflict", CodeOf(putStale));
        Assert.Equal("concurrency.conflict", CodeOf(deleteStale));
    }

    [Fact]
    public async Task ChangingTheFirstRecordDateIs422FirstRecordDateFixed()
    {
        var scene = await SceneAsync();
        using var tenant = scene.Tenant;

        var problem = await ProblemAsync(
            await RewriteAsync(
                tenant, scene.Detail, IdAt(scene.Detail, "2026-01-15"), "2026-01-16", scene.Setup.PositionId, scene.Setup.AreaId, scene.Setup.WorkplaceId),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.assignment.first_record_date_fixed", CodeOf(problem));
    }

    [Fact]
    public async Task ADateOutsideItsNeighboursIs422OutOfOrder()
    {
        var scene = await SceneAsync();
        using var tenant = scene.Tenant;

        var problem = await ProblemAsync(
            await RewriteAsync(
                tenant, scene.Detail, IdAt(scene.Detail, "2026-03-10"), "2026-01-15", scene.Coordinator, scene.Setup.AreaId, scene.Setup.WorkplaceId),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.assignment.effective_date_out_of_order", CodeOf(problem));
    }

    [Fact]
    public async Task ARewriteEqualToANeighbourIs422Unchanged()
    {
        var scene = await SceneAsync();
        using var tenant = scene.Tenant;

        var problem = await ProblemAsync(
            await RewriteAsync(
                tenant, scene.Detail, IdAt(scene.Detail, "2026-03-10"), "2026-03-10", scene.Setup.PositionId, scene.Setup.AreaId, scene.Setup.WorkplaceId),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.assignment.unchanged", CodeOf(problem));
    }

    [Fact]
    public async Task AWorkplaceChangeMidMonthIs422()
    {
        var scene = await SceneAsync();
        using var tenant = scene.Tenant;
        var office = await CreatedAsync(
            tenant.Hr,
            $"{tenant.Url}/workplaces",
            new { name = "Oficina", siteId = scene.Setup.SiteId, ciiuCode = "4711", riskClass = "I", highRiskPension = false });

        var problem = await ProblemAsync(
            await RewriteAsync(
                tenant, scene.Detail, IdAt(scene.Detail, "2026-03-10"), "2026-03-10", scene.Coordinator, scene.Setup.AreaId, office.GetProperty("id").GetGuid()),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.assignment.site_change_not_first_of_month", CodeOf(problem));
    }

    [Fact]
    public async Task AWorkplaceOfAnotherCompanyIs422WithItsField()
    {
        var scene = await SceneAsync();
        using var tenant = scene.Tenant;
        var second = await SeedSecondCompanyAsync(tenant);

        var problem = await ProblemAsync(
            await RewriteAsync(
                tenant, scene.Detail, IdAt(scene.Detail, "2026-03-10"), "2026-03-01", scene.Coordinator, scene.Setup.AreaId, second.WorkplaceId),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.assignment.workplace_other_company", CodeOf(problem));
        Assert.True(HasError(problem, "workplaceId"));
    }

    [Fact]
    public async Task ADateAfterTheContractEndIs422OnEffectiveDate()
    {
        var scene = await SceneAsync();
        using var tenant = scene.Tenant;

        var problem = await ProblemAsync(
            await RewriteAsync(
                tenant, scene.Detail, IdAt(scene.Detail, "2026-03-10"), "2027-02-01", scene.Coordinator, scene.Setup.AreaId, scene.Setup.WorkplaceId),
            HttpStatusCode.UnprocessableEntity);

        Assert.Equal("validation.failed", CodeOf(problem));
        Assert.True(HasError(problem, "effectiveDate"));
    }

    // Propuesta 6: conservar un cargo archivado después se permite; elegir otro archivado, no.
    [Fact]
    public async Task KeepingAnArchivedPositionIsAllowedButChoosingAnotherIs422()
    {
        var scene = await SceneAsync();
        using var tenant = scene.Tenant;
        var id = IdAt(scene.Detail, "2026-03-10");
        var sales = (await CreatedAsync(tenant.Hr, $"{tenant.Url}/areas", new { name = "Ventas" })).GetProperty("id").GetGuid();
        var spare = await PositionAsync(tenant, "Auxiliar");
        foreach (var position in new[] { scene.Coordinator, spare })
        {
            Assert.Equal(HttpStatusCode.OK, (await tenant.Hr.PostAsync($"{tenant.Url}/positions/{position}/archive", null, Ct)).StatusCode);
        }

        var kept = await OkAsync(await RewriteAsync(tenant, scene.Detail, id, "2026-03-10", scene.Coordinator, sales, scene.Setup.WorkplaceId));
        var other = await ProblemAsync(
            await RewriteAsync(tenant, kept, id, "2026-03-10", spare, sales, scene.Setup.WorkplaceId), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("Ventas", RecordAt(kept, "2026-03-10").GetProperty("area").GetProperty("name").GetString());
        Assert.True(HasError(other, "positionId"));
    }

    [Fact]
    public async Task AnUnknownRecordIs404AndATerminatedContractIs422()
    {
        var scene = await SceneAsync();
        using var tenant = scene.Tenant;
        var id = IdAt(scene.Detail, "2026-03-10");

        var unknownPut = await ProblemAsync(
            await RewriteAsync(tenant, scene.Detail, Guid.NewGuid(), "2026-03-10", scene.Setup.PositionId, null, scene.Setup.WorkplaceId),
            HttpStatusCode.NotFound);
        var unknownDelete = await ProblemAsync(await DeleteAsync(tenant, scene.Detail, Guid.NewGuid()), HttpStatusCode.NotFound);
        var terminated = await OkAsync(await tenant.Hr.PostAsJsonAsync(
            $"{ContractUrl(tenant, scene.Detail)}/terminate", new { reason = "Resignation", date = "2026-11-30", notes = (string?)null }, Ct));
        var inactivePut = await ProblemAsync(
            await RewriteAsync(
                tenant, scene.Detail, id, "2026-03-10", scene.Setup.PositionId, null, scene.Setup.WorkplaceId, ifMatch: Version(terminated)),
            HttpStatusCode.UnprocessableEntity);
        var inactiveDelete = await ProblemAsync(
            await DeleteAsync(tenant, scene.Detail, id, ifMatch: Version(terminated)), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.assignment.not_found", CodeOf(unknownPut));
        Assert.Equal("hr.assignment.not_found", CodeOf(unknownDelete));
        Assert.Equal("hr.contract.not_active", CodeOf(inactivePut));
        Assert.Equal("hr.contract.not_active", CodeOf(inactiveDelete));
    }

    [Fact]
    public async Task DeletingRemovesTheRecordAndLeavesATrace()
    {
        var scene = await SceneAsync();
        using var tenant = scene.Tenant;

        var deleted = await OkAsync(await DeleteAsync(tenant, scene.Detail, IdAt(scene.Detail, "2026-03-10")));

        var remaining = Assert.Single(Assignments(deleted).EnumerateArray());
        Assert.Equal("2026-01-15", remaining.GetProperty("effectiveDate").GetString());
        Assert.Equal(VersionOf(scene.Detail) + 1, VersionOf(deleted));
        Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, "hr.contract.assignment_deleted", ContractIdOf(scene.Detail)));
        var activity = await JsonOfAsync(await tenant.Hr.GetAsync($"{EmployeeUrl(tenant, scene.Detail)}/activity", Ct));
        Assert.Equal("AssignmentDeleted", activity.GetProperty("items")[0].GetProperty("action").GetString());
        var reloaded = await GetEmployeeAsync(tenant, scene.Detail);
        Assert.Equal(VersionOf(deleted), VersionOf(reloaded));
        Assert.Equal(1, Assignments(reloaded).GetArrayLength());
    }

    [Fact]
    public async Task TheFirstRecordIs422FirstRecordNotDeletable()
    {
        var scene = await SceneAsync();
        using var tenant = scene.Tenant;

        var problem = await ProblemAsync(
            await DeleteAsync(tenant, scene.Detail, IdAt(scene.Detail, "2026-01-15")), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.assignment.first_record_not_deletable", CodeOf(problem));
    }

    // Review Focus 4: la ficha ya decía canDelete en false y el DELETE no muta nada.
    [Fact]
    public async Task DeletingAMiddleRecordThatLeavesEqualNeighboursIs422()
    {
        var scene = await SceneAsync();
        using var tenant = scene.Tenant;
        var detail = await OkAsync(await AddAssignmentAsync(
            tenant, scene.Detail, "2026-05-20", scene.Setup.PositionId, scene.Setup.AreaId, scene.Setup.WorkplaceId));
        Assert.Equal((true, false), FlagsOf(RecordAt(detail, "2026-03-10")));

        var problem = await ProblemAsync(await DeleteAsync(tenant, detail, IdAt(detail, "2026-03-10")), HttpStatusCode.UnprocessableEntity);

        Assert.Equal("hr.assignment.unchanged", CodeOf(problem));
        var after = await GetEmployeeAsync(tenant, detail);
        Assert.Equal(VersionOf(detail), VersionOf(after));
        Assert.Equal(3, Assignments(after).GetArrayLength());
    }

    [Fact]
    public async Task DeletingARecordWithAPdfWritesTheReleaseEvent()
    {
        var scene = await SceneAsync();
        using var tenant = scene.Tenant;
        var id = IdAt(scene.Detail, "2026-03-10");
        var fileId = _fixture.Documents.Seed(Guid.CreateVersion7(), HrDocumentState.Available);
        var attached = await OkAsync(await tenant.Hr.PutAsJsonAsync(
            $"{ContractUrl(tenant, scene.Detail)}/assignments/{id}/document", new { fileId }, Ct));

        await OkAsync(await DeleteAsync(tenant, attached, id));

        var payload = JsonDocument.Parse(Assert.Single(await ReleaseEventsAsync(tenant))).RootElement;
        Assert.Equal(tenant.Id, payload.GetProperty("tenantId").GetGuid());
        Assert.Equal([fileId], payload.GetProperty("fileIds").EnumerateArray().Select(value => value.GetGuid()));

        var other = await SceneAsync();
        using var otherTenant = other.Tenant;
        await OkAsync(await DeleteAsync(otherTenant, other.Detail, IdAt(other.Detail, "2026-03-10")));
        Assert.Empty(await ReleaseEventsAsync(otherTenant));
    }
}
