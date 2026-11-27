using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static Task<HttpResponseMessage> AddAssignmentAsync(
        HrTenant tenant, JsonElement detail, string effectiveDate, Guid positionId, Guid areaId, Guid workplaceId) =>
        tenant.Hr.PostAsJsonAsync(
            $"{ContractUrl(tenant, detail)}/assignments", new { effectiveDate, positionId, areaId, workplaceId }, Ct);

    private static JsonElement Assignments(JsonElement detail) => detail.GetProperty("contract").GetProperty("assignments");

    private static (bool CanEdit, bool CanDelete) FlagsOf(JsonElement record) =>
        (record.GetProperty("canEdit").GetBoolean(), record.GetProperty("canDelete").GetBoolean());

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
}
