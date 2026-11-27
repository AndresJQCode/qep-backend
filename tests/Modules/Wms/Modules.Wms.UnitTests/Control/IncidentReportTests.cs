using Modules.Wms.Domain;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests.Control;

public sealed class IncidentReportTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Member = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static Incident Report(
        IncidentSubject? subject = null,
        string? note = null,
        string? rawCode = null,
        IReadOnlyList<IncidentEvidence>? evidence = null,
        Guid? reportedBy = null,
        DateTimeOffset? reportedAt = null) =>
        Incident.Report(
            Tenant,
            "NOV-20261126-0001",
            WarehouseId.New(),
            OwnerId.New(),
            IncidentType.ShortMove,
            subject ?? IncidentSubject.Empty,
            note,
            rawCode,
            null,
            evidence ?? [],
            reportedBy ?? Member,
            "zebra-01",
            "key-1",
            reportedAt ?? Now,
            Now.AddMinutes(1));

    [Fact]
    public void ReportingIsOpenWithVersionOneAndAReportedEvent()
    {
        var task = Guid.NewGuid();
        var line = Guid.NewGuid();
        var location = LocationId.New();
        var variant = Guid.NewGuid();
        var subject = new IncidentSubject(task, line, location, variant, null, StockStatus.Available, 5, 2);
        var scanned = Now.AddMinutes(-3).ToOffset(TimeSpan.FromHours(-5));

        var incident = Report(subject, reportedAt: scanned);

        Assert.Equal("NOV-20261126-0001", incident.Number);
        Assert.Equal((IncidentStatus.Open, 1L), (incident.Status, incident.Version));
        Assert.Equal((task, line, location, variant), (incident.SourceTaskId, incident.SourceTaskLineId, incident.LocationId, incident.VariantId));
        Assert.Equal((5m, 2m), (incident.ExpectedQty, incident.ActualQty));
        Assert.Equal(scanned, incident.ReportedAt);
        Assert.Equal(TimeSpan.Zero, incident.ReportedAt.Offset);
        Assert.Equal((Member, "zebra-01", "key-1"), (incident.ReportedBy, incident.DeviceId, incident.OperationKey));
        var reported = Assert.Single(incident.Events);
        Assert.Equal(IncidentEventKind.Reported, reported.Kind);
        Assert.Equal(Member, reported.MemberId);
        Assert.Equal(scanned, reported.OccurredAt);
        Assert.Equal(Now.AddMinutes(1), reported.RecordedAt);
    }

    [Fact]
    public void TheSystemReportsWithoutAMember()
    {
        var incident = Report(reportedBy: Guid.Empty);

        Assert.Equal(Guid.Empty, incident.ReportedBy);
        Assert.Null(Assert.Single(incident.Events).MemberId);
    }

    [Fact]
    public void TheNoteAndTheRawCodeAreTrimmedAndBounded()
    {
        var incident = Report(note: "  faltan dos  ", rawCode: " 7701234 ");

        Assert.Equal(("faltan dos", "7701234"), (incident.Note, incident.RawCode));
        Assert.Equal(
            WmsErrorCodes.IncidentInvalid,
            Assert.Throws<WmsDomainException>(() => Report(note: new string('a', Incident.NoteMaxLength + 1))).Code);
        Assert.Equal(
            WmsErrorCodes.IncidentInvalid,
            Assert.Throws<WmsDomainException>(() => Report(rawCode: new string('1', Incident.RawCodeMaxLength + 1))).Code);
        Assert.Equal(500, Report(note: new string('a', 500)).Note!.Length);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -0.5)]
    public void NegativeQuantitiesAreInvalid(double expected, double actual)
    {
        var subject = IncidentSubject.Empty with { ExpectedQty = (decimal)expected, ActualQty = (decimal)actual };

        Assert.Equal(WmsErrorCodes.IncidentInvalid, Assert.Throws<WmsDomainException>(() => Report(subject)).Code);
    }

    [Fact]
    public void MoreThanFiveEvidenceFilesIsEvidenceInvalid()
    {
        IncidentEvidence[] six =
            [.. Enumerable.Range(0, Incident.MaxEvidence + 1).Select(_ => IncidentEvidence.Create(Guid.NewGuid(), IncidentEvidenceKind.Photo, null))];

        Assert.Equal(WmsErrorCodes.IncidentEvidenceInvalid, Assert.Throws<WmsDomainException>(() => Report(evidence: six)).Code);
        var five = Report(evidence: [.. six.Take(Incident.MaxEvidence)]);
        Assert.Equal([0, 1, 2, 3, 4], five.Evidence.Select(value => value.Position));
    }
}
