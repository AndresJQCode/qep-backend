using Modules.Wms.Domain.Control;

namespace Modules.Wms.UnitTests.Control;

public sealed class IncidentCatalogTests
{
    [Theory]
    [InlineData(IncidentType.ShortMove)]
    [InlineData(IncidentType.LocationMismatch)]
    [InlineData(IncidentType.UnreadableLabel)]
    public void TheFloorTypesAreResolvedByAccepting(IncidentType type) =>
        Assert.Equal([IncidentResolution.Accept], IncidentCatalog.ResolutionsOf(type));

    [Fact]
    public void ASyncConflictIsResolvedByAcceptingItIntoStock() =>
        Assert.Equal([IncidentResolution.AcceptIntoStock], IncidentCatalog.ResolutionsOf(IncidentType.SyncConflict));

    [Fact]
    public void ACountVarianceHasNoResolutionBecauseItsAdjustmentClosesIt() =>
        Assert.Empty(IncidentCatalog.ResolutionsOf(IncidentType.CountVariance));

    [Theory]
    [InlineData(IncidentType.SyncConflict, true)]
    [InlineData(IncidentType.ShortMove, false)]
    [InlineData(IncidentType.LocationMismatch, false)]
    [InlineData(IncidentType.UnreadableLabel, false)]
    [InlineData(IncidentType.CountVariance, false)]
    public void OnlyASyncConflictCanBeRejected(IncidentType type, bool expected) =>
        Assert.Equal(expected, IncidentCatalog.CanBeRejected(type));

    [Theory]
    [InlineData(IncidentType.ShortMove, true)]
    [InlineData(IncidentType.LocationMismatch, true)]
    [InlineData(IncidentType.UnreadableLabel, false)]
    [InlineData(IncidentType.CountVariance, false)]
    [InlineData(IncidentType.SyncConflict, false)]
    public void OnlyAShortAndALocationMismatchRequestAVerification(IncidentType type, bool expected) =>
        Assert.Equal(expected, IncidentCatalog.RequestsVerification(type));

    [Fact]
    public void AMoveTaskDeclaresAShortOrALocationMismatch() =>
        Assert.Equal(
            [IncidentType.ShortMove, IncidentType.LocationMismatch],
            IncidentCatalog.DeclarableOnShort("Move").Order());

    [Theory]
    [InlineData("Count")]
    [InlineData("Pick")]
    [InlineData("")]
    public void AnyOtherTaskTypeDeclaresNothingOnAShort(string taskType) =>
        Assert.Empty(IncidentCatalog.DeclarableOnShort(taskType));

    [Fact]
    public void AnUnreadableLabelAndALocationMismatchCanBeReportedAlone() =>
        Assert.Equal(
            [IncidentType.LocationMismatch, IncidentType.UnreadableLabel],
            IncidentCatalog.DeclarableStandalone.Order());

    // Un tipo nuevo sin fila en el catálogo se pone en rojo aquí, no en producción.
    [Fact]
    public void EveryTypeHasAnEntry()
    {
        foreach (var type in Enum.GetValues<IncidentType>())
        {
            var exception = Record.Exception(() =>
            {
                _ = IncidentCatalog.ResolutionsOf(type);
                _ = IncidentCatalog.CanBeRejected(type);
                _ = IncidentCatalog.RequestsVerification(type);
            });

            Assert.True(exception is null, $"{type} has no catalog entry: {exception?.Message}");
        }

        Assert.Empty(IncidentCatalog.DeclarableOnShort("Move").Except(Enum.GetValues<IncidentType>()));
        Assert.Empty(IncidentCatalog.DeclarableStandalone.Except(Enum.GetValues<IncidentType>()));
    }
}
