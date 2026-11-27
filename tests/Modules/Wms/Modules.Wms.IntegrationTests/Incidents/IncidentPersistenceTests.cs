using System.Net;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;
using Npgsql;
using static Modules.Wms.IntegrationTests.Incidents.IncidentTestSupport;
using static Modules.Wms.IntegrationTests.Tasks.TaskTestSupport;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests.Incidents;

/// <summary>La persistencia de novedades contra PostgreSQL real (spec 2026-11-26 §8.1): la migración <c>AddIncidentsAndShortLines</c>,
/// sus <c>CHECK</c>, el índice de la clave de operación y la numeración.</summary>
public sealed class IncidentPersistenceTests
{
    private static readonly TimeSpan StoragePrecision = TimeSpan.FromMilliseconds(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnIncidentRoundTripsWithEvidenceAndEvents()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var lot = await CreateLotAsync(factory, s, s.VariantId, "L-1");
        var task = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        var reporter = Guid.CreateVersion7();
        var scanned = DateTimeOffset.UtcNow.AddMinutes(-2);
        var subject = new IncidentSubject(task, line, new LocationId(s.BinA), s.VariantId, new LotId(lot), StockStatus.Quarantine, 5, 2.5m);
        var evidence = new[]
        {
            IncidentEvidence.Create(Guid.CreateVersion7(), IncidentEvidenceKind.Photo, null),
            IncidentEvidence.Create(Guid.CreateVersion7(), IncidentEvidenceKind.Scan, "7701234567890"),
        };
        var incident = await NumberAndSaveAsync(
            factory,
            s,
            number => NewIncident(
                s, number, IncidentType.LocationMismatch, subject, "Faltan unidades", "XYZ-1", """{"action":"confirm","quantity":2}""",
                evidence, reporter, "key-1", reportedAt: scanned));

        var reloaded = await ReloadAsync(factory, incident);

        Assert.Equal(incident.Number, reloaded.Number);
        Assert.Equal((IncidentType.LocationMismatch, IncidentStatus.Open, 1L), (reloaded.Type, reloaded.Status, reloaded.Version));
        Assert.Equal((new WarehouseId(s.WarehouseId), new OwnerId(s.OwnerId)), (reloaded.WarehouseId, reloaded.OwnerId));
        Assert.Equal(
            (task, line, new LocationId(s.BinA), s.VariantId, new LotId(lot), (StockStatus?)StockStatus.Quarantine),
            (reloaded.SourceTaskId, reloaded.SourceTaskLineId, reloaded.LocationId, reloaded.VariantId, reloaded.LotId, reloaded.StockStatus));
        Assert.Equal((5m, 2.5m), (reloaded.ExpectedQty, reloaded.ActualQty));
        Assert.Equal(("XYZ-1", "Faltan unidades", reporter, "zebra-01", "key-1"),
            (reloaded.RawCode, reloaded.Note, reloaded.ReportedBy, reloaded.DeviceId, reloaded.OperationKey));
        Assert.Equal(scanned, reloaded.ReportedAt, StoragePrecision);
        Assert.Null(reloaded.Resolution);
        Assert.Null(reloaded.VerificationTaskId);
        Assert.Equal(
            evidence.Select(value => (value.Id, value.FileId, value.Kind, value.RawValue, value.Position)),
            reloaded.Evidence.OrderBy(value => value.Position).Select(value => (value.Id, value.FileId, value.Kind, value.RawValue, value.Position)));
        var reported = Assert.Single(reloaded.Events);
        Assert.Equal((IncidentEventKind.Reported, (Guid?)reporter), (reported.Kind, reported.MemberId));

        // raw_payload es jsonb de verdad: se consulta por clave.
        Assert.Equal("jsonb", await ScalarAsync<string>(database, "SELECT pg_typeof(raw_payload)::text FROM wms.incidents"));
        Assert.Equal("confirm", await ScalarAsync<string>(database, "SELECT raw_payload->>'action' FROM wms.incidents"));
    }

    [Fact]
    public async Task TheOperationKeyIsUniquePerTenant()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SaveAsync(factory, NewIncident(s, "NOV-A-1", operationKey: "key-1"));

        var exception = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => SaveAsync(factory, NewIncident(s, "NOV-A-2", operationKey: "key-1")));

        Assert.Equal("concurrency.conflict", exception.Code);
        // Otro tenant puede usar la misma clave, y las novedades sin clave no chocan entre sí.
        await SaveAsync(factory, NewIncident(s, "NOV-B-1", operationKey: "key-1", tenantId: Guid.Parse(OtherTenantId)));
        await SaveAsync(factory, NewIncident(s, "NOV-A-3"), NewIncident(s, "NOV-A-4"));
        Assert.Equal(4L, await ScalarAsync<long>(database, "SELECT count(*) FROM wms.incidents"));
    }

    [Fact]
    public async Task ADuplicatedNumberTranslatesTo412()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        await SaveAsync(factory, NewIncident(s, "NOV-A-1"));

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => SaveAsync(factory, NewIncident(s, "NOV-A-1")));
    }

    [Fact]
    public async Task NumbersAreSequentialPerTenantAndDay()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var first = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number));
        var second = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number));

        Assert.Equal(NumberOf(today, 1), first.Number);
        Assert.Equal(NumberOf(today, 2), second.Number);
        await using var other = factory.Services.CreateAsyncScope();
        var unit = other.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();
        await using var open = await unit.BeginTransactionAsync(Ct);
        Assert.Equal(
            NumberOf(today, 1),
            await other.ServiceProvider.GetRequiredService<IIncidentRepository>()
                .NextNumberAsync(Guid.Parse(OtherTenantId), today, Ct));
    }

    [Fact]
    public async Task OnlyTheOwnTenantReadsTheIncident()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var incident = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, operationKey: "key-1"));

        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IIncidentRepository>();

        Assert.NotNull(await repository.GetWithEventsAsync(s.TenantId, incident.Id, Ct));
        Assert.Null(await repository.GetWithEventsAsync(Guid.Parse(OtherTenantId), incident.Id, Ct));
        Assert.True(await repository.ExistsWithOperationKeyAsync(s.TenantId, "key-1", Ct));
        Assert.False(await repository.ExistsWithOperationKeyAsync(Guid.Parse(OtherTenantId), "key-1", Ct));
        Assert.False(await repository.ExistsWithOperationKeyAsync(s.TenantId, "key-2", Ct));
    }

    [Theory]
    [InlineData("type", "CK_incidents_type")]
    [InlineData("status", "CK_incidents_status")]
    [InlineData("resolution", "CK_incidents_resolution")]
    public async Task TheDatabaseRejectsAnUnknownTypeStatusOrResolution(string column, string constraint)
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var incident = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(database, $"UPDATE wms.incidents SET {column} = 'Nope' WHERE id = @id", ("id", incident.Id)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    [Fact]
    public async Task AResolvedIncidentNeedsItsResolutionAndDate()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var incident = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(database, "UPDATE wms.incidents SET status = 'Resolved' WHERE id = @id", ("id", incident.Id)));

        Assert.Equal("CK_incidents_resolved", exception.ConstraintName);
    }

    [Fact]
    public async Task ListOpenVerifiedByAsyncSkipsClosedIncidents()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var count = Guid.CreateVersion7();
        var open = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number));
        var inReview = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number));
        var resolved = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number));
        var other = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number));
        await ExecuteAsync(
            database,
            "UPDATE wms.incidents SET verification_task_id = @count WHERE id IN (@open, @review, @resolved)",
            ("count", count), ("open", open.Id), ("review", inReview.Id), ("resolved", resolved.Id));
        await ExecuteAsync(database, "UPDATE wms.incidents SET status = 'InReview' WHERE id = @id", ("id", inReview.Id));
        await ExecuteAsync(
            database,
            "UPDATE wms.incidents SET status = 'Resolved', resolution = 'Accept', resolved_at = now() WHERE id = @id",
            ("id", resolved.Id));
        await using var scope = factory.Services.CreateAsyncScope();
        var incidents = scope.ServiceProvider.GetRequiredService<IIncidentRepository>();

        var verified = await incidents.ListOpenVerifiedByAsync(s.TenantId, count, Ct);

        Assert.Equal(
            new[] { open.Id, inReview.Id }.Order(),
            verified.Select(value => value.Id).Order());
        Assert.Empty(await incidents.ListOpenVerifiedByAsync(Guid.CreateVersion7(), count, Ct));
        Assert.DoesNotContain(other.Id, verified.Select(value => value.Id));
    }

    [Fact]
    public async Task FindByAdjustmentAsyncFindsTheCountVarianceOfThatTenantOnly()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var adjustment = Guid.CreateVersion7();
        var variance = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, IncidentType.CountVariance));
        await NumberAndSaveAsync(factory, s, number => NewIncident(s, number));
        await ExecuteAsync(
            database, "UPDATE wms.incidents SET adjustment_id = @adjustment WHERE id = @id", ("adjustment", adjustment), ("id", variance.Id));
        await using var scope = factory.Services.CreateAsyncScope();
        var incidents = scope.ServiceProvider.GetRequiredService<IIncidentRepository>();

        var found = await incidents.FindByAdjustmentAsync(s.TenantId, adjustment, Ct);

        Assert.Equal(variance.Id, found?.Id);
        Assert.Null(await incidents.FindByAdjustmentAsync(Guid.CreateVersion7(), adjustment, Ct));
        Assert.Null(await incidents.FindByAdjustmentAsync(s.TenantId, Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task ListReportersOfChildrenAsyncReturnsTheDistinctReportersOfTheChildren()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var parent = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, IncidentType.CountVariance));
        var childOfFirst = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, reportedBy: first));
        var secondChild = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, reportedBy: second));
        var againFirst = await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, reportedBy: first));
        await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, reportedBy: Guid.CreateVersion7()));
        await ExecuteAsync(
            database,
            "UPDATE wms.incidents SET parent_incident_id = @parent WHERE id IN (@a, @b, @c)",
            ("parent", parent.Id), ("a", childOfFirst.Id), ("b", secondChild.Id), ("c", againFirst.Id));
        await using var scope = factory.Services.CreateAsyncScope();
        var incidents = scope.ServiceProvider.GetRequiredService<IIncidentRepository>();

        var reporters = await incidents.ListReportersOfChildrenAsync(s.TenantId, parent.Id, Ct);

        Assert.Equal(new[] { first, second }.Order(), reporters.Order());
        Assert.Empty(await incidents.ListReportersOfChildrenAsync(Guid.CreateVersion7(), parent.Id, Ct));
    }

    [Fact]
    public async Task DeletingALocationUsedByAnIncidentIs422HasStock()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var location = await CreateDestinationAsync(s);
        var subject = new IncidentSubject(null, null, new LocationId(location), null, null, null, null, null);
        await NumberAndSaveAsync(factory, s, number => NewIncident(s, number, IncidentType.UnreadableLabel, subject));
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"{WmsUrl()}/warehouses/{s.WarehouseId}/locations/{location}");

        var response = await s.Manager.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.location.has_stock", await CodeOfAsync(response));
    }
}
