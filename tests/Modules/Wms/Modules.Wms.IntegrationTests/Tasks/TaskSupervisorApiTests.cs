using System.Net;
using System.Text.Json;
using Modules.Wms.Application;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Tasks;

/// <summary>Crear y leer tareas por la API (spec 2026-11-26 §6.11) contra el esquema real.</summary>
public sealed class TaskSupervisorApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // B5 (owner, 2026-11-27): el día del número es el del huso del tenant (el arnés usa America/Bogota), no el de UTC.
    [Fact]
    public async Task TheDailyNumberCutsTheDayInTheTenantTimeZone()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");
        string DayOf(DateTimeOffset instant) =>
            TimeZoneInfo.ConvertTime(instant, bogota).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var before = DayOf(DateTimeOffset.UtcNow);

        var task = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination)]));

        var after = DayOf(DateTimeOffset.UtcNow);
        Assert.Contains(task.Number, new[] { $"TSK-{before}-0001", $"TSK-{after}-0001" });
    }

    [Fact]
    public async Task CreatingAMoveReturns201ReadyWithItsLines()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);

        var response = await CreateRawAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination)]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = await ReadAsync<TaskDto>(response);
        Assert.Equal($"/api/v1/tenants/{TenantId}/wms/tasks/{task.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Ready", task.Status);
        Assert.Matches(@"^TSK-\d{8}-0001$", task.Number);
        var line = Assert.Single(task.Lines);
        Assert.Equal(("A-01-02", "B-01-01"), (line.FromLocationCode, line.ToLocationCode));
        Assert.Equal(5m, line.QuantityRemaining);
        // El perfil por defecto nace en la misma petición que la tarea.
        Assert.Equal("UND", line.BaseUom);
        Assert.Null(task.LeaseToken);
        Assert.Empty(task.Events);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.task.created", task.Id.ToString()));
    }

    [Fact]
    public async Task RepeatingTheKeyReturnsTheSavedTaskWith200()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var body = Body(s, [Line(s.VariantId, s.BinA, await CreateDestinationAsync(s))]);
        var key = Guid.NewGuid().ToString();

        var first = await CreateRawAsync(s.Manager, body, key);
        var second = await CreateRawAsync(s.Manager, body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((await ReadAsync<TaskDto>(first)).Id, (await ReadAsync<TaskDto>(second)).Id);
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.tasks"));
    }

    [Fact]
    public async Task TheSameKeyWithAnotherBodyIs409()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var key = Guid.NewGuid().ToString();
        await CreateRawAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination)]), key);

        var response = await CreateRawAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 6)]), key);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("wms.idempotency_key_reused", await CodeOfAsync(response));
    }

    [Fact]
    public async Task CreatingWithoutTheKeyIs428()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var body = Body(s, [Line(s.VariantId, s.BinA, await CreateDestinationAsync(s))]);

        var response = await s.Manager.PostAsync(TasksUrl(), System.Net.Http.Json.JsonContent.Create(body), Ct);

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
        Assert.Equal("wms.idempotency_key_required", await CodeOfAsync(response));
    }

    [Fact]
    public async Task CreatingWithoutTaskManageIs403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var body = Body(s, [Line(s.VariantId, s.BinA, await CreateDestinationAsync(s))]);
        using var worker = CreateClient(factory, SubjectId, TenantId, WmsPermissions.DocumentRead, WmsPermissions.PutawayPerform);

        var response = await CreateRawAsync(worker, body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreatingForAnotherTenantIs403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var body = Body(s, [Line(s.VariantId, s.BinA, await CreateDestinationAsync(s))]);
        using var stranger = CreateClient(factory, OtherSubjectId, OtherTenantId, AllPermissions);

        var response = await CreateRawAsync(stranger, body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AVirtualForeignOrRepeatedLocationIs422LocationInvalid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var other = await CreateWarehouseAsync(s.Manager, "MDE");
        var foreign = (await CreateBinAsync(s.Manager, other.Id, "C", "C-01-01")).Id;
        var zone = (await ListLocationsAsync(s.Manager, s.WarehouseId)).Single(location => location.Code == "B").Id;

        foreach (var line in new[]
        {
            Line(s.VariantId, s.BinA, s.Adjustment),
            Line(s.VariantId, foreign, destination),
            Line(s.VariantId, s.BinA, zone),
            Line(s.VariantId, s.BinA, s.BinA),
        })
        {
            var response = await CreateRawAsync(s.Manager, Body(s, [line]));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("wms.task.location_invalid", await CodeOfAsync(response));
        }

        // El origen sí puede ser un muelle.
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.Receiving, destination)]));
    }

    [Fact]
    public async Task ALotOfAnotherVariantIs404LotNotFound()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var otherVariant = await CreateVariantAsync(factory, "V2", null, [12]);
        var lot = await CreateLotAsync(factory, s, otherVariant, "L-1");

        var response = await CreateRawAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, lotId: lot)]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("wms.lot.not_found", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AnInvalidBodyIs422WithTheFieldErrors()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);

        var response = await CreateRawAsync(
            s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 0)], note: new string('n', 501)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await BodyAsync(response));
        var errors = problem.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("Note", out var _note));
        Assert.True(errors.TryGetProperty("Lines[0].Quantity", out var _quantity));
    }

    [Fact]
    public async Task AssigningToAMemberWithoutThePutawayPermissionIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var (advisor, advisorClient) = await SeedOperatorAsync(factory, roles: ["advisor"]);
        var (worker, workerClient) = await SeedOperatorAsync(factory, SecondOperatorSubject);
        using var _a = advisorClient;
        using var _w = workerClient;

        var rejected = await CreateRawAsync(
            s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination)], assignTo: advisor));
        var accepted = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination)], assignTo: worker));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal("wms.task.assignee_not_eligible", await CodeOfAsync(rejected));
        Assert.Equal("Assigned", accepted.Status);
        Assert.Equal(worker, accepted.AssignedMemberId);
        Assert.Equal("Caller", accepted.AssignedMemberName);
    }

    [Fact]
    public async Task ListingFiltersByStatusAndPages()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination)]));
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination)]));
        await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination)], publish: false));
        using var reader = CreateReader(factory);

        var response = await reader.GetAsync(
            $"{TasksUrl()}?warehouseId={s.WarehouseId}&status=Ready&type=Move&page=2&pageSize=1", Ct);
        var invalid = await reader.GetAsync($"{TasksUrl()}?status=Nada", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await ReadAsync<TaskPageDto>(response);
        Assert.Equal((2, 1, 2), (page.Page, page.PageSize, page.TotalCount));
        Assert.EndsWith("-0001", Assert.Single(page.Items).Number);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
    }

    [Fact]
    public async Task GettingATaskIncludesItsEvents()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var created = await CreateAsync(
            s.Manager, Body(s, [Line(s.VariantId, s.BinA, await CreateDestinationAsync(s))]));
        using var reader = CreateReader(factory);

        var task = await GetAsync(reader, created.Id);

        Assert.Equal(["Created", "Published"], task.Events.Select(value => value.Kind));
        Assert.Equal("Caller", task.Events[0].MemberName);
    }

    [Fact]
    public async Task AnUnknownTaskIs404TaskNotFound()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await ArrangeAsync(database, factory);
        using var reader = CreateReader(factory);

        var response = await reader.GetAsync($"{TasksUrl()}/{Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("wms.task.not_found", await CodeOfAsync(response));
    }
}
