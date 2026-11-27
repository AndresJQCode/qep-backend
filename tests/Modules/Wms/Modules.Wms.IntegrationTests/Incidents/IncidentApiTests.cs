using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
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
    public async Task ReportingALocationMismatchWithAVariantCreatesACount()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;

        var response = await ReportRawAsync(client, ReportBody(s, "LocationMismatch", variant: s.VariantId), NewKey());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadAsync<IncidentDto>(response);
        var countId = await ScalarAsync<Guid>(database, "SELECT id FROM wms.tasks WHERE type = 'Count'");
        Assert.Equal(countId, dto.VerificationTaskId);
        var count = await GetTaskAsync(s.Manager, countId);
        Assert.Equal(("Ready", "Incident", dto.Id), (count.Status, count.SourceType, count.SourceId));
        Assert.Equal(("A-01-02", "Available"), (count.Lines[0].FromLocationCode, count.Lines[0].StockStatus));
        Assert.Contains(dto.Events, value => value.Kind == "VerificationLinked");
    }

    [Fact]
    public async Task ALocationMismatchWithoutAVariantHasNoCount()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;

        var response = await ReportRawAsync(client, ReportBody(s, "LocationMismatch"), NewKey());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null((await ReadAsync<IncidentDto>(response)).VerificationTaskId);
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.tasks WHERE type = 'Count'"));
    }

    private static Task<TaskDto> GetTaskAsync(HttpClient client, Guid id) => GetAsync(client, id);

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

    // ───── review, resolve y reject (spec §8.3 y §8.5) ─────

    private static async Task<HttpResponseMessage> IncidentActionAsync(HttpClient client, Guid id, string action, object body) =>
        await client.PostAsJsonAsync($"{IncidentsUrl()}/{id}/{action}", body, Ct);

    private static async Task<Guid> SupervisorMemberAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<IMembershipDirectory>();
        return await directory.FindActiveMembershipIdAsync(Guid.Parse(SubjectId), Guid.Parse(TenantId), Ct)
            ?? throw new InvalidOperationException("The supervisor has no membership.");
    }

    private sealed record Conflict(LedgerSetup Setup, Guid Destination, TaskDto Task, Guid Reporter, HttpClient ReporterClient, Incident Incident);

    /// <summary>10 en A-01-02, un movimiento de 5 a B-01-01 y una novedad <c>SyncConflict</c> del operario por <paramref name="found"/>.</summary>
    private static async Task<Conflict> ArrangeConflictAsync(PostgreSqlContainer database, QepApiFactory factory, decimal found = 3)
    {
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await StockAsync(factory, s, s.BinA, 10);
        var task = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5)]));
        var (reporter, client) = await SeedOperatorAsync(factory);
        var subject = new IncidentSubject(
            task.Id, task.Lines[0].Id, new LocationId(s.BinA), s.VariantId, null, StockStatus.Available, null, found);
        var incident = await NumberAndSaveAsync(
            factory,
            s,
            number => NewIncident(s, number, IncidentType.SyncConflict, subject, reportedBy: reporter, operationKey: NewKey()));
        return new Conflict(s, destination, task, reporter, client, incident);
    }

    private static Task<long> IncidentMovementsAsync(PostgreSqlContainer database) =>
        ScalarAsync<long>(database, "SELECT count(*) FROM wms.stock_movements WHERE document_type = 'Incident'");

    [Fact]
    public async Task ReviewThenAcceptRoundTrips()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var incident = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, IncidentType.UnreadableLabel));
        var supervisor = await SupervisorMemberAsync(factory);

        var review = await IncidentActionAsync(s.Manager, incident.Id, "review", new { version = 1 });
        var reviewed = await ReadAsync<IncidentDto>(review);
        var resolve = await IncidentActionAsync(s.Manager, incident.Id, "resolve", new { version = 2, resolution = "Accept", note = "Se acepta" });
        var resolution = await ReadAsync<IncidentResolutionDto>(resolve);
        var stored = await GetIncidentAsync(s.Manager, incident.Id);

        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        Assert.Equal(("InReview", 2L), (reviewed.Status, reviewed.Version));
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        Assert.Null(resolution.BatchId);
        Assert.Empty(resolution.CreatedTaskIds);
        Assert.Equal(("Resolved", 3L, "Accept", "Se acepta"), (stored.Status, stored.Version, stored.Resolution, stored.ResolutionNote));
        Assert.Equal((supervisor, "Caller"), (stored.ResolvedBy, stored.ResolvedByName));
        Assert.Equal(["Reported", "InReview", "Resolved"], stored.Events.Select(value => value.Kind));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.incident.reviewed", incident.Id.ToString()));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.incident.resolved", incident.Id.ToString()));
        Assert.Equal(0L, await IncidentMovementsAsync(database));
    }

    [Fact]
    public async Task AStaleVersionIs412()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var incident = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, IncidentType.UnreadableLabel));

        var review = await IncidentActionAsync(s.Manager, incident.Id, "review", new { version = 7 });
        var resolve = await IncidentActionAsync(s.Manager, incident.Id, "resolve", new { version = 7, resolution = "Accept" });
        var reject = await IncidentActionAsync(s.Manager, incident.Id, "reject", new { version = 7, note = "x" });

        Assert.Equal(HttpStatusCode.PreconditionFailed, review.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, resolve.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, reject.StatusCode);
        var stored = await GetIncidentAsync(s.Manager, incident.Id);
        Assert.Equal(("Open", 1L), (stored.Status, stored.Version));
        Assert.Equal(0, await CountAuditEntriesAsync(factory, "wms.incident.reviewed", incident.Id.ToString()));
    }

    [Fact]
    public async Task TwoConcurrentReviewsLeaveOneWinnerAndOneStale()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var incident = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, IncidentType.UnreadableLabel));

        var responses = await Task.WhenAll(
            IncidentActionAsync(s.Manager, incident.Id, "review", new { version = 1 }),
            IncidentActionAsync(s.Manager, incident.Id, "review", new { version = 1 }));

        var codes = responses.Select(response => response.StatusCode).Order().ToList();
        Assert.Equal(HttpStatusCode.OK, codes[0]);
        Assert.True(
            codes[1] is HttpStatusCode.PreconditionFailed or HttpStatusCode.UnprocessableEntity,
            $"The loser answered {codes[1]}.");
        var stored = await GetIncidentAsync(s.Manager, incident.Id);
        Assert.Equal(("InReview", 2L), (stored.Status, stored.Version));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.incident.reviewed", incident.Id.ToString()));
    }

    [Fact]
    public async Task ResolvingWithANotAllowedResolutionIs422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var label = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, IncidentType.UnreadableLabel));
        var variance = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, IncidentType.CountVariance));

        var intoStock = await IncidentActionAsync(s.Manager, label.Id, "resolve", new { version = 1, resolution = "AcceptIntoStock" });
        var byHand = await IncidentActionAsync(s.Manager, variance.Id, "resolve", new { version = 1, resolution = "Accept" });
        var unknown = await IncidentActionAsync(s.Manager, label.Id, "resolve", new { version = 1, resolution = "Nope" });
        var missing = await IncidentActionAsync(s.Manager, label.Id, "resolve", new { version = 1 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, intoStock.StatusCode);
        Assert.Equal("wms.incident.resolution_not_allowed", await CodeOfAsync(intoStock));
        Assert.Equal("wms.incident.resolution_not_allowed", await CodeOfAsync(byHand));
        Assert.Equal("validation.failed", await CodeOfAsync(unknown));
        Assert.Equal("validation.failed", await CodeOfAsync(missing));
        Assert.Equal("Open", (await GetIncidentAsync(s.Manager, label.Id)).Status);
        Assert.Equal(0L, await IncidentMovementsAsync(database));
    }

    [Fact]
    public async Task ResolvingOrRejectingFromAClosedStateIs422InvalidTransition()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var incident = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, IncidentType.UnreadableLabel));
        var conflict = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, IncidentType.SyncConflict));
        await IncidentActionAsync(s.Manager, incident.Id, "resolve", new { version = 1, resolution = "Accept" });
        await IncidentActionAsync(s.Manager, conflict.Id, "reject", new { version = 1, note = "duplicada" });

        var again = await IncidentActionAsync(s.Manager, incident.Id, "resolve", new { version = 2, resolution = "Accept" });
        var reviewResolved = await IncidentActionAsync(s.Manager, incident.Id, "review", new { version = 2 });
        var resolveRejected = await IncidentActionAsync(s.Manager, conflict.Id, "resolve", new { version = 2, resolution = "AcceptIntoStock" });
        var rejectAgain = await IncidentActionAsync(s.Manager, conflict.Id, "reject", new { version = 2, note = "otra vez" });

        foreach (var response in new[] { again, reviewResolved, resolveRejected, rejectAgain })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("wms.document.invalid_transition", await CodeOfAsync(response));
        }

        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.incident.resolved", incident.Id.ToString()));
    }

    [Fact]
    public async Task RejectingAShortMoveIs422ResolutionNotAllowed()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var incident = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, IncidentType.ShortMove));

        var response = await IncidentActionAsync(s.Manager, incident.Id, "reject", new { version = 1, note = "no cuenta" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.incident.resolution_not_allowed", await CodeOfAsync(response));
        Assert.Equal("Open", (await GetIncidentAsync(s.Manager, incident.Id)).Status);
        Assert.Equal(0, await CountAuditEntriesAsync(factory, "wms.incident.rejected", incident.Id.ToString()));
    }

    [Fact]
    public async Task RejectingASyncConflictWithANote()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var incident = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, IncidentType.SyncConflict));

        var withoutNote = await IncidentActionAsync(s.Manager, incident.Id, "reject", new { version = 1 });
        var blank = await IncidentActionAsync(s.Manager, incident.Id, "reject", new { version = 1, note = "  " });
        var rejected = await IncidentActionAsync(s.Manager, incident.Id, "reject", new { version = 1, note = "Duplicada" });

        Assert.Equal("validation.failed", await CodeOfAsync(withoutNote));
        Assert.Equal("validation.failed", await CodeOfAsync(blank));
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        var dto = await ReadAsync<IncidentDto>(rejected);
        Assert.Equal(("Rejected", 2L, "Duplicada"), (dto.Status, dto.Version, dto.ResolutionNote));
        Assert.Null(dto.Resolution);
        Assert.Empty(dto.AllowedResolutions);
        Assert.False(dto.CanBeRejected);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.incident.rejected", incident.Id.ToString()));
    }

    [Fact]
    public async Task AcceptingASyncConflictIntoStockMovesTheStock()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeConflictAsync(database, factory);
        using var _c = floor.ReporterClient;
        var s = floor.Setup;
        var before = await GetAsync(s.Manager, floor.Task.Id);

        var response = await IncidentActionAsync(s.Manager, floor.Incident.Id, "resolve", new { version = 1, resolution = "AcceptIntoStock" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resolution = await ReadAsync<IncidentResolutionDto>(response);
        Assert.NotNull(resolution.BatchId);
        Assert.Equal(7m, await OnHandAsync(database, s.BinA, s.VariantId, StockStatus.Available));
        Assert.Equal(3m, await OnHandAsync(database, floor.Destination, s.VariantId, StockStatus.Available));
        Assert.Equal(1L, await IncidentMovementsAsync(database));
        Assert.Equal(
            resolution.BatchId,
            await ScalarAsync<Guid>(database, "SELECT batch_id FROM wms.stock_movements WHERE document_type = 'Incident'"));
        Assert.Equal(
            floor.Task.Lines[0].Id,
            await ScalarAsync<Guid>(database, "SELECT document_line_id FROM wms.stock_movements WHERE document_type = 'Incident'"));
        var after = await GetAsync(s.Manager, floor.Task.Id);
        Assert.Equal((before.Version, before.Status, before.Lines[0].QuantityConfirmed), (after.Version, after.Status, after.Lines[0].QuantityConfirmed));
        var stored = await GetIncidentAsync(s.Manager, floor.Incident.Id);
        Assert.Equal(("Resolved", "AcceptIntoStock", resolution.BatchId), (stored.Status, stored.Resolution, stored.BatchId));
        Assert.Equal(await SupervisorMemberAsync(factory), stored.ResolvedBy);
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.incident.resolved", floor.Incident.Id.ToString()));
    }

    [Fact]
    public async Task TheReporterCannotAcceptIntoStockTheirOwnConflict()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeConflictAsync(database, factory);
        using var _c = floor.ReporterClient;
        using var reporter = CreateClient(
            factory, OperatorSubject, TenantId, WmsPermissions.DocumentRead, WmsPermissions.PutawayPerform, WmsPermissions.DiscrepancyResolve);

        var response = await IncidentActionAsync(reporter, floor.Incident.Id, "resolve", new { version = 1, resolution = "AcceptIntoStock" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.incident.self_resolution", await CodeOfAsync(response));
        Assert.Equal(0L, await IncidentMovementsAsync(database));
        Assert.Equal("Open", (await GetIncidentAsync(reporter, floor.Incident.Id)).Status);
        Assert.Equal(10m, await OnHandAsync(database, floor.Setup.BinA, floor.Setup.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task AConflictWithNothingFoundCannotBeAcceptedIntoStock()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeConflictAsync(database, factory, found: 0);
        using var _c = floor.ReporterClient;

        var response = await IncidentActionAsync(floor.Setup.Manager, floor.Incident.Id, "resolve", new { version = 1, resolution = "AcceptIntoStock" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.incident.resolution_not_allowed", await CodeOfAsync(response));
        Assert.Equal(0L, await IncidentMovementsAsync(database));
        var stored = await GetIncidentAsync(floor.Setup.Manager, floor.Incident.Id);
        Assert.Equal("Open", stored.Status);
        Assert.Empty(stored.AllowedResolutions);
        Assert.True(stored.CanBeRejected);
    }

    [Fact]
    public async Task RepeatingTheResolveReturnsTheSavedResponse()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var floor = await ArrangeConflictAsync(database, factory);
        using var _c = floor.ReporterClient;
        var body = new { version = 1, resolution = "AcceptIntoStock" };

        var first = await IncidentActionAsync(floor.Setup.Manager, floor.Incident.Id, "resolve", body);
        var second = await IncidentActionAsync(floor.Setup.Manager, floor.Incident.Id, "resolve", body);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((await ReadAsync<IncidentResolutionDto>(first)).BatchId, (await ReadAsync<IncidentResolutionDto>(second)).BatchId);
        Assert.Equal(1L, await IncidentMovementsAsync(database));
        Assert.Equal(7m, await OnHandAsync(database, floor.Setup.BinA, floor.Setup.VariantId, StockStatus.Available));
    }

    [Fact]
    public async Task ReviewResolveAndRejectWithoutDiscrepancyResolveAre403()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var incident = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, IncidentType.SyncConflict));
        var (_, operatorClient) = await SeedOperatorAsync(factory);
        using var _c = operatorClient;

        foreach (var (action, body) in new (string, object)[]
        {
            ("review", new { version = 1 }),
            ("resolve", new { version = 1, resolution = "Accept" }),
            ("reject", new { version = 1, note = "x" }),
        })
        {
            var response = await IncidentActionAsync(operatorClient, incident.Id, action, body);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Equal(("Open", 1L), ((await GetIncidentAsync(s.Manager, incident.Id)).Status, 1L));
    }

    [Fact]
    public async Task AnUnknownIncidentIs404OnEveryAction()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var unknown = Guid.NewGuid();

        foreach (var (action, body) in new (string, object)[]
        {
            ("review", new { version = 1 }),
            ("resolve", new { version = 1, resolution = "Accept" }),
            ("reject", new { version = 1, note = "x" }),
        })
        {
            var response = await IncidentActionAsync(s.Manager, unknown, action, body);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("wms.incident.not_found", await CodeOfAsync(response));
        }
    }

    [Fact]
    public async Task OperatorActionsLeaveNoAudit()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var destination = await CreateDestinationAsync(s);
        await StockAsync(factory, s, s.BinA, 10);
        var task = await CreateAsync(s.Manager, Body(s, [Line(s.VariantId, s.BinA, destination, 5)]));
        var (_, client) = await SeedOperatorAsync(factory);
        using var _c = client;
        var claimed = await ReadAsync<TaskDto>(
            await client.PostAsJsonAsync($"{TasksUrl()}/next", new { warehouseId = s.WarehouseId, deviceId = "zebra-01" }, Ct));
        var reported = await ReadAsync<IncidentDto>(await ReportRawAsync(client, ReportBody(s)));

        var shortResponse = await ShortRawAsync(
            client, claimed.Id, claimed.Lines[0].Id, ShortBody(claimed.LeaseToken!.Value, 2), NewKey());

        Assert.Equal(HttpStatusCode.OK, shortResponse.StatusCode);
        var shortIncident = Assert.Single(await IncidentsOfTaskAsync(database, task.Id));
        foreach (var id in new[] { reported.Id, shortIncident.Id })
        {
            foreach (var action in new[] { "reviewed", "resolved", "rejected", "reported", "created" })
            {
                Assert.Equal(0, await CountAuditEntriesAsync(factory, $"wms.incident.{action}", id.ToString()));
            }
        }

        Assert.Equal(
            0L,
            await ScalarAsync<long>(database, "SELECT count(*) FROM audit.entries WHERE action LIKE 'wms.incident.%'"));
    }
}
