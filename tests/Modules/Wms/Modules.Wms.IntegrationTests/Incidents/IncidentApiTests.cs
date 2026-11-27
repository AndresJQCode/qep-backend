using System.Net;
using System.Net.Http.Json;
using Modules.Wms.Application;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.Incidents.IncidentTestSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskApiSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Incidents;

/// <summary>Declarar y leer novedades, y las acciones del supervisor sobre ellas (spec 2026-11-26 §8.3 a §8.5).</summary>
public sealed class IncidentApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string IncidentsUrl() => $"{WmsUrl()}/incidents";

    private static string NewKey() => Guid.NewGuid().ToString();

    private static object ReportBody(
        LedgerSetup s,
        string type = "UnreadableLabel",
        Guid? location = null,
        Guid? variant = null,
        Guid? lot = null,
        string? note = "Etiqueta rota",
        IReadOnlyList<Guid>? photos = null,
        Guid? sourceTask = null,
        DateTimeOffset? occurredAt = null) =>
        new
        {
            warehouseId = s.WarehouseId,
            type,
            locationId = location ?? s.BinA,
            variantId = variant,
            lotId = lot,
            rawCode = "7701234",
            note,
            photoFileIds = photos,
            sourceTaskId = sourceTask,
            occurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            deviceId = "zebra-01",
        };

    private static async Task<HttpResponseMessage> ReportRawAsync(HttpClient client, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, IncidentsUrl()) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? NewKey());
        return await client.SendAsync(request, Ct);
    }

    private static async Task<IncidentDto> GetIncidentAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"{IncidentsUrl()}/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<IncidentDto>(response);
    }

    private static Task<long> IncidentCountAsync(PostgreSqlContainer database) =>
        ScalarAsync<long>(database, "SELECT count(*) FROM wms.incidents");

    [Fact]
    public async Task ReportingAnUnreadableLabelReturns201Open()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (member, client) = await SeedOperatorAsync(factory);
        using var _c = client;
        var photo = await SeedEvidenceFileAsync(factory, s.TenantId);
        var key = NewKey();
        var scanned = DateTimeOffset.UtcNow.AddMinutes(-2);

        var response = await ReportRawAsync(client, ReportBody(s, photos: [photo], occurredAt: scanned), key);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadAsync<IncidentDto>(response);
        Assert.EndsWith($"/incidents/{dto.Id}", response.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal(("UnreadableLabel", "Open", 1L), (dto.Type, dto.Status, dto.Version));
        Assert.Equal((member, "A-01-02", s.WarehouseId, s.OwnerId), (dto.ReportedBy, dto.LocationCode, dto.WarehouseId, dto.OwnerId));
        Assert.Equal(scanned, dto.ReportedAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(("7701234", "Etiqueta rota", "zebra-01"), (dto.RawCode, dto.Note, dto.DeviceId));
        Assert.Equal(photo, Assert.Single(dto.Evidence).FileId);
        Assert.Equal("Reported", Assert.Single(dto.Events).Kind);
        Assert.Equal(["Accept"], dto.AllowedResolutions);
        Assert.False(dto.CanBeRejected);
        Assert.Equal(dto.Id, await ScalarAsync<Guid>(database, "SELECT id FROM wms.incidents WHERE operation_key = @key", ("key", key)));
    }

    [Fact]
    public async Task RepeatingTheReportReturnsTheSavedResponseWith200()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;
        var body = ReportBody(s);
        var key = NewKey();

        var first = await ReportRawAsync(client, body, key);
        var second = await ReportRawAsync(client, body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((await ReadAsync<IncidentDto>(first)).Id, (await ReadAsync<IncidentDto>(second)).Id);
        Assert.Equal(1L, await IncidentCountAsync(database));
    }

    [Fact]
    public async Task ReportingWithoutAnyTaskPermissionIs403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SeedOperatorAsync(factory);
        using var reader = CreateClient(factory, OperatorSubject, TenantId, WmsPermissions.DocumentRead);

        var response = await ReportRawAsync(reader, ReportBody(s));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(response));
        Assert.Equal(0L, await IncidentCountAsync(database));
    }

    [Theory]
    [InlineData("ShortMove")]
    [InlineData("CountVariance")]
    [InlineData("SyncConflict")]
    public async Task ReportingATypeThatNeedsALineIs422TypeNotAllowed(string type)
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;

        var response = await ReportRawAsync(client, ReportBody(s, type));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.incident.type_not_allowed", await CodeOfAsync(response));
        Assert.Equal(0L, await IncidentCountAsync(database));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task ReportingWithoutANoteIs422ValidationFailed(string? note)
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;

        var response = await ReportRawAsync(client, ReportBody(s, note: note));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
    }

    [Fact]
    public async Task ALocationOfAnotherWarehouseIs404()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;
        var other = await CreateWarehouseAsync(s.Manager, "MDE");
        var foreignBin = await CreateBinAsync(s.Manager, other.Id, "A", "A-01-01");

        var response = await ReportRawAsync(client, ReportBody(s, location: foreignBin.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("wms.location.not_found", await CodeOfAsync(response));
        Assert.Equal(0L, await IncidentCountAsync(database));
    }

    [Fact]
    public async Task AnUnknownVariantIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;

        var response = await ReportRawAsync(client, ReportBody(s, variant: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.profile.variant_unknown", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AUnknownLotIs404AndALotWithoutItsVariantIsInvalid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;

        var unknown = await ReportRawAsync(client, ReportBody(s, variant: s.VariantId, lot: Guid.NewGuid()));
        var orphan = await ReportRawAsync(client, ReportBody(s, lot: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("wms.lot.not_found", await CodeOfAsync(unknown));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, orphan.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(orphan));
        Assert.Equal(0L, await IncidentCountAsync(database));
    }

    [Fact]
    public async Task AnUnknownSourceTaskIs404()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;

        var response = await ReportRawAsync(client, ReportBody(s, sourceTask: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("wms.task.not_found", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AScanOlderThanThirtyDaysIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;

        var response = await ReportRawAsync(client, ReportBody(s, occurredAt: DateTimeOffset.UtcNow.AddDays(-31)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.movement.occurred_at_out_of_range", await CodeOfAsync(response));
    }

    [Fact]
    public async Task ReportingWithAPhotoOfAnotherTenantIs422EvidenceInvalid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;
        await SeedTenantAsync(factory, OtherTenantId);
        var foreign = await SeedEvidenceFileAsync(factory, Guid.Parse(OtherTenantId));

        var response = await ReportRawAsync(client, ReportBody(s, photos: [foreign]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.incident.evidence_invalid", await CodeOfAsync(response));
        Assert.Equal(0L, await IncidentCountAsync(database));
    }

    [Fact]
    public async Task ReportingWithSixPhotosIs422EvidenceInvalid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;
        var photos = new List<Guid>();
        for (var index = 0; index < 6; index++)
        {
            photos.Add(await SeedEvidenceFileAsync(factory, s.TenantId));
        }

        var response = await ReportRawAsync(client, ReportBody(s, photos: photos));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.incident.evidence_invalid", await CodeOfAsync(response));
    }

    [Fact]
    public async Task ListingFiltersByWarehouseStatusAndTypeNewestFirst()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var other = await CreateWarehouseAsync(s.Manager, "MDE");
        var otherBin = await CreateBinAsync(s.Manager, other.Id, "A", "A-01-01");
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        var oldest = await NumberAndSaveAsync(
            factory,
            s,
            number =>
            {
                var incident = NewIncident(s, number, IncidentType.UnreadableLabel, reportedAt: t0);
                incident.Review(Guid.NewGuid(), DateTimeOffset.UtcNow);
                return incident;
            },
            day);
        var middle = await NumberAndSaveAsync(
            factory, s, number => NewIncident(s, number, IncidentType.SyncConflict, reportedAt: t0.AddHours(1)), day);
        var newest = await NumberAndSaveAsync(
            factory, s, number => NewIncident(s, number, IncidentType.UnreadableLabel, reportedAt: t0.AddHours(2)), day);
        var elsewhere = await NumberAndSaveAsync(
            factory,
            s,
            number => NewIncident(
                s,
                number,
                IncidentType.UnreadableLabel,
                new IncidentSubject(null, null, new LocationId(otherBin.Id), null, null, null, null, null),
                reportedAt: t0.AddHours(2.5),
                warehouseId: other.Id),
            day);

        var all = await ListAsync(s.Manager, $"warehouseId={s.WarehouseId}");
        var byStatus = await ListAsync(s.Manager, $"warehouseId={s.WarehouseId}&status=InReview");
        var byType = await ListAsync(s.Manager, $"warehouseId={s.WarehouseId}&type=SyncConflict");
        var otherWarehouse = await ListAsync(s.Manager, $"warehouseId={other.Id}");
        var unfiltered = await ListAsync(s.Manager, string.Empty);
        var paged = await ListAsync(s.Manager, $"warehouseId={s.WarehouseId}&page=2&pageSize=2");

        Assert.Equal([newest.Id, middle.Id, oldest.Id], all.Items.Select(item => item.Id));
        Assert.Equal(3, all.TotalCount);
        Assert.Equal([oldest.Id], byStatus.Items.Select(item => item.Id));
        Assert.Equal("InReview", byStatus.Items[0].Status);
        Assert.Equal([middle.Id], byType.Items.Select(item => item.Id));
        Assert.Equal([elsewhere.Id], otherWarehouse.Items.Select(item => item.Id));
        Assert.Equal(("A-01-01", 4), (otherWarehouse.Items[0].LocationCode, unfiltered.TotalCount));
        Assert.Equal([oldest.Id], paged.Items.Select(item => item.Id));
        Assert.Equal((2, 2, 3), (paged.Page, paged.PageSize, paged.TotalCount));
        var badStatus = await s.Manager.GetAsync($"{IncidentsUrl()}?status=Nope", Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badStatus.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(badStatus));
    }

    [Fact]
    public async Task GettingIncludesEvidenceEventsAllowedResolutionsAndNames()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        var task = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5)]));
        var (member, client) = await SeedOperatorAsync(factory);
        using var _c = client;
        var photo = await SeedEvidenceFileAsync(factory, s.TenantId);
        var evidence = new[] { IncidentEvidence.Create(photo, IncidentEvidenceKind.Photo, null) };
        var subject = new IncidentSubject(
            task.Id, task.Lines[0].Id, new LocationId(s.BinA), s.VariantId, null, StockStatus.Available, null, 3);
        var found = await NumberAndSaveAsync(
            factory,
            s,
            number => NewIncident(
                s, number, IncidentType.SyncConflict, subject, note: "Confirmó sin reclamo", evidence: evidence, reportedBy: member));
        var nothingFound = await NumberAndSaveAsync(
            factory,
            s,
            number => NewIncident(
                s,
                number,
                IncidentType.SyncConflict,
                subject with { ActualQty = 0 },
                reportedBy: member));
        var system = await NumberAndSaveAsync(
            factory, s, number => NewIncident(s, number, IncidentType.UnreadableLabel, reportedBy: Guid.Empty));

        var dto = await GetIncidentAsync(client, found.Id);
        var empty = await GetIncidentAsync(client, nothingFound.Id);
        var bySystem = await GetIncidentAsync(client, system.Id);

        Assert.Equal(["AcceptIntoStock"], dto.AllowedResolutions);
        Assert.True(dto.CanBeRejected);
        Assert.Equal((task.Number, "A-01-02", "Caller"), (dto.SourceTaskNumber, dto.LocationCode, dto.ReportedByName));
        Assert.Equal(("V1", s.VariantId), (dto.VariantCode, dto.VariantId));
        Assert.Equal("Available", dto.StockStatus);
        Assert.Equal(photo, Assert.Single(dto.Evidence).FileId);
        var reported = Assert.Single(dto.Events);
        Assert.Equal(("Reported", member, "Caller"), (reported.Kind, reported.MemberId, reported.MemberName));
        Assert.Empty(empty.AllowedResolutions);
        Assert.True(empty.CanBeRejected);
        Assert.Equal("Sistema", bySystem.ReportedByName);
        Assert.Equal(["Accept"], bySystem.AllowedResolutions);
        Assert.False(bySystem.CanBeRejected);
    }

    [Fact]
    public async Task AnUnknownIncidentIs404()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);

        var response = await s.Manager.GetAsync($"{IncidentsUrl()}/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("wms.incident.not_found", await CodeOfAsync(response));
    }

    private static async Task<IncidentPageDto> ListAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"{IncidentsUrl()}?{query}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<IncidentPageDto>(response);
    }
}
