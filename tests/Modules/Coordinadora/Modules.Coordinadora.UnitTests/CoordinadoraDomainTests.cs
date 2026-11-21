using Modules.Coordinadora.Domain;

namespace Modules.Coordinadora.UnitTests;

/// <summary>Spec 2026-11-21 §5.2: la configuración de la conexión. Toda escritura sube la versión (es el <c>If-Match</c> del <c>PUT</c>), el
/// recaudo nace apagado y los faltantes para la guía salen del agregado.</summary>
public sealed class CoordinadoraSettingsTests
{
    private static readonly Guid City = Guid.Parse("0193c0de-0001-7000-8000-000000000001");

    private static CoordinadoraSettings NewSettings() => CoordinadoraSettings.Create(Guid.CreateVersion7(), Guid.CreateVersion7());

    internal static CoordinadoraSettingsInput CompleteInput(CoordinadoraPackage? package = null) => new(
        "Bodega Ritual",
        "Calle 43 3-23",
        "3170044722",
        City,
        "2",
        "0",
        1,
        null,
        package ?? CoordinadoraPackage.Create(10, 20, 30, 1.5m, 1),
        CoordinadoraSettings.PackagingAutomatic,
        0m,
        false,
        null);

    [Fact]
    public void ANewSettingsIsEmptyWithCollectionOffAndVersionZero()
    {
        var settings = NewSettings();

        Assert.Equal(0, settings.Version);
        Assert.False(settings.CollectOnDelivery);
        Assert.Equal(CoordinadoraSettings.PackagingAutomatic, settings.PackagingType);
        Assert.Equal(0m, settings.DeclaredValueMinimum);
        Assert.Null(settings.DefaultPackage);
        Assert.Equal(
            [CoordinadoraSettingsReadiness.SenderIncomplete, CoordinadoraSettingsReadiness.PackageDefaultsMissing],
            settings.MissingForShipping());
    }

    [Fact]
    public void EveryUpdateBumpsTheVersionAndACompleteOneHasNothingMissing()
    {
        var settings = NewSettings();

        settings.Update(CompleteInput());
        Assert.Equal(1, settings.Version);
        settings.Update(CompleteInput() with { CollectOnDelivery = true, DeclaredValueMinimum = 20000m });
        Assert.Equal(2, settings.Version);

        Assert.Empty(settings.MissingForShipping());
        Assert.True(settings.CollectOnDelivery);
        Assert.Equal(20000m, settings.DeclaredValueMinimum);
        Assert.Equal(new CoordinadoraPackage(10, 20, 30, 1.5m, 1), settings.DefaultPackage);
    }

    [Fact]
    public void TextsAreTrimmedAndEmptyOnesBecomeNull()
    {
        var settings = NewSettings();

        settings.Update(CompleteInput() with { SenderName = "  Bodega  ", Div = "   ", LabelFormat = "" });

        Assert.Equal("Bodega", settings.SenderName);
        Assert.Null(settings.Div);
        Assert.Null(settings.LabelFormat);
    }

    [Theory]
    [InlineData("senderName", 121)]
    [InlineData("accountCode", 21)]
    public void AValueLongerThanItsColumnIsRejectedWithTheField(string field, int length)
    {
        var settings = NewSettings();
        var value = new string('x', length);
        var input = field == "senderName" ? CompleteInput() with { SenderName = value } : CompleteInput() with { AccountCode = value };

        var error = Assert.Throws<CoordinadoraDomainException>(() => settings.Update(input));

        Assert.Equal(CoordinadoraErrorCodes.SettingsValueInvalid, error.Code);
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, settings.Version);
    }

    [Fact]
    public void APackagingTypeOutsideTheTwoKnownValuesIsRejected() =>
        Assert.Equal(
            CoordinadoraErrorCodes.SettingsValueInvalid,
            Assert.Throws<CoordinadoraDomainException>(() => NewSettings().Update(CompleteInput() with { PackagingType = 2 })).Code);

    [Fact]
    public void ADeclaredValueMinimumWithCentsOrNegativeIsRejected()
    {
        Assert.Throws<CoordinadoraDomainException>(() => NewSettings().Update(CompleteInput() with { DeclaredValueMinimum = 10.5m }));
        Assert.Throws<CoordinadoraDomainException>(() => NewSettings().Update(CompleteInput() with { DeclaredValueMinimum = -1m }));
    }

    [Fact]
    public void WithoutAPackageTheDefaultsAreClearedAndReportedMissing()
    {
        var settings = NewSettings();
        settings.Update(CompleteInput());

        settings.Update(CompleteInput(package: null) with { DefaultPackage = null });

        Assert.Null(settings.DefaultPackage);
        Assert.Equal([CoordinadoraSettingsReadiness.PackageDefaultsMissing], settings.MissingForShipping());
    }
}

/// <summary>Spec 2026-11-21 §5.3: el paquete que viaja en <c>detalle</c>; los topes frenan un cero o un valor imposible antes de gastar una llamada.</summary>
public sealed class CoordinadoraPackageTests
{
    [Theory]
    [InlineData(0, 10, 10, 1.0, 1)]
    [InlineData(10, 301, 10, 1.0, 1)]
    [InlineData(10, 10, 10, 0.0, 1)]
    [InlineData(10, 10, 10, 1000.0, 1)]
    [InlineData(10, 10, 10, 1.123, 1)]
    [InlineData(10, 10, 10, 1.0, 0)]
    [InlineData(10, 10, 10, 1.0, 100)]
    public void AValueOutOfRangeIsRejected(int height, int width, int length, double weight, int units) =>
        Assert.Equal(
            CoordinadoraErrorCodes.PackageInvalid,
            Assert.Throws<CoordinadoraDomainException>(() => CoordinadoraPackage.Create(height, width, length, (decimal)weight, units)).Code);

    [Fact]
    public void AValidPackageKeepsItsValues() =>
        Assert.Equal(new CoordinadoraPackage(10, 20, 300, 999.99m, 99), CoordinadoraPackage.Create(10, 20, 300, 999.99m, 99));
}

/// <summary>Los recortes y la comparación con «Coordinadora» que comparten el armador y los agregados.</summary>
public sealed class CoordinadoraTextTests
{
    [Theory]
    [InlineData("Coordinadora", true)]
    [InlineData("COORDINADORA MERCANTIL", true)]
    [InlineData("coordinadóra", true)]
    [InlineData("Servientrega", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCoordinadoraIgnoresCaseAndAccents(string? name, bool expected) => Assert.Equal(expected, CoordinadoraText.IsCoordinadora(name));

    [Fact]
    public void CapTrimsAndCutsWithoutSplittingASurrogatePair()
    {
        Assert.Null(CoordinadoraText.Cap("   ", 10));
        Assert.Equal("abc", CoordinadoraText.Cap("  abc  ", 10));
        Assert.Equal("ab", CoordinadoraText.Cap("abcd", 2));
        // «ab» más un emoji de dos UTF-16: cortar en 3 partiría el par, así que queda en 2.
        Assert.Equal("ab", CoordinadoraText.Cap("ab\U0001F600cd", 3));
    }
}

/// <summary>Spec 2026-11-21 §5.4 y §5.5: la guía. Sin reenvío: una petición que no se sabe si llegó queda <c>Unknown</c> y la resuelve una persona.</summary>
public sealed class CoordinadoraShipmentTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static CoordinadoraShipment Pending() =>
        CoordinadoraShipment.PendingFor(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), """{"referencia":"PED-1"}""", 90000m, 3, Guid.CreateVersion7(), At);

    [Fact]
    public void APendingShipmentIsLiveAndNotYetCreated()
    {
        var shipment = Pending();

        Assert.Equal(CoordinadoraShipmentStatus.Pending, shipment.Status);
        Assert.True(shipment.IsLive);
        Assert.False(shipment.IsCreated);
        Assert.Equal(0, shipment.Attempts);
        Assert.Equal(90000m, shipment.DeclaredValue);
        Assert.Equal(3, shipment.OrderVersion);
    }

    [Fact]
    public void BeginSendClaimsTheRowForTheLease()
    {
        var shipment = Pending();

        shipment.BeginSend(At);

        Assert.Equal(1, shipment.Attempts);
        Assert.True(shipment.IsBeingSent(At.AddMinutes(1)));
        Assert.False(shipment.IsBeingSent(At.Add(CoordinadoraShipment.SendLease)));
    }

    [Fact]
    public void APendingWhoseLeaseExpiredBecomesUnknownNotResent()
    {
        var shipment = Pending();
        shipment.BeginSend(At);

        Assert.False(shipment.ExpireIfStale(At.AddMinutes(1)));
        Assert.True(shipment.ExpireIfStale(At.AddMinutes(3)));

        Assert.Equal(CoordinadoraShipmentStatus.Unknown, shipment.Status);
        Assert.Equal("send_lease_expired", shipment.ErrorCode);
        Assert.True(shipment.IsLive);
        Assert.False(shipment.ExpireIfStale(At.AddMinutes(10)));
    }

    [Fact]
    public void MarkCreatedKeepsTheGuideAndTheLabelAndClearsTheError()
    {
        var shipment = Pending();
        shipment.BeginSend(At);

        shipment.MarkCreated("12345678901", [0x25, 0x50, 0x44, 0x46], At.AddSeconds(5));

        Assert.Equal(CoordinadoraShipmentStatus.Created, shipment.Status);
        Assert.True(shipment.IsCreated);
        Assert.Equal("12345678901", shipment.GuideNumber);
        Assert.NotNull(shipment.LabelPdf);
        Assert.Null(shipment.SendingSince);
        Assert.Null(shipment.ErrorCode);
    }

    [Fact]
    public void MarkRejectedCapsTheDetailAndFreesTheOrder()
    {
        var shipment = Pending();

        shipment.MarkRejected("fault", new string('x', 500), At);

        Assert.Equal(CoordinadoraShipmentStatus.Rejected, shipment.Status);
        Assert.False(shipment.IsLive);
        Assert.Equal(CoordinadoraShipment.ErrorDetailMaxLength, shipment.ErrorDetail!.Length);
    }

    [Fact]
    public void AnUnknownIsResolvedAsExistingOrMissing()
    {
        var exists = Pending();
        exists.MarkUnknown("timeout", At);
        exists.ResolveExists(" 11111111111 ", At.AddHours(1));
        Assert.Equal(CoordinadoraShipmentStatus.Created, exists.Status);
        Assert.Equal("11111111111", exists.GuideNumber);
        Assert.Null(exists.LabelPdf);

        var missing = Pending();
        missing.MarkUnknown("timeout", At);
        missing.ResolveMissing(At.AddHours(1));
        Assert.Equal(CoordinadoraShipmentStatus.Failed, missing.Status);
        Assert.False(missing.IsLive);
    }

    [Fact]
    public void ResolvingNeedsAnUnknownAndExistsNeedsANumber()
    {
        var pending = Pending();
        Assert.Equal(
            CoordinadoraErrorCodes.ShipmentStateInvalid,
            Assert.Throws<CoordinadoraDomainException>(() => pending.ResolveMissing(At)).Code);

        var unknown = Pending();
        unknown.MarkUnknown("timeout", At);
        Assert.Equal(
            CoordinadoraErrorCodes.ShipmentGuideNumberRequired,
            Assert.Throws<CoordinadoraDomainException>(() => unknown.ResolveExists("  ", At)).Code);
    }

    [Fact]
    public void OnlyACreatedGuideCanBeCancelledTrackedOrPrinted()
    {
        var pending = Pending();
        var by = Guid.CreateVersion7();

        Assert.Equal(CoordinadoraErrorCodes.ShipmentStateInvalid, Assert.Throws<CoordinadoraDomainException>(() => pending.MarkCancelled(by, At)).Code);
        Assert.Equal(CoordinadoraErrorCodes.ShipmentStateInvalid, Assert.Throws<CoordinadoraDomainException>(() => pending.RecordTracking("x", null, At)).Code);

        pending.MarkCreated("12345678901", [1, 2, 3], At);
        pending.RecordTracking("EN REPARTO", "Salió a reparto", At.AddHours(2));
        Assert.Equal(("EN REPARTO", "Salió a reparto", At.AddHours(2)), (pending.TrackingStatus, pending.TrackingDetail, pending.TrackedAt));

        pending.MarkCancelled(by, At.AddHours(3));
        Assert.Equal(CoordinadoraShipmentStatus.Cancelled, pending.Status);
        Assert.False(pending.IsLive);
        Assert.Null(pending.LabelPdf);
        Assert.Equal((by, At.AddHours(3)), (pending.CancelledBy, pending.CancelledAt));
    }

    [Fact]
    public void ToStringNeverWritesTheRequest()
    {
        var shipment = Pending();

        Assert.DoesNotContain("PED-1", shipment.ToString(), StringComparison.Ordinal);
    }
}
