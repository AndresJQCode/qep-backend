using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests;

public sealed class VariantLogisticsProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static ProfileChanges Changes(
        TrackingPolicy tracking = TrackingPolicy.None,
        decimal? weightKg = null,
        params (string Code, decimal Factor, string? Barcode)[] conversions) =>
        new(UomCode.Parse("UND"), 0, tracking, false, weightKg, null, null, null, null, null, null, conversions);

    private static VariantLogisticsProfile NewProfile(ProfileChanges? changes = null) =>
        VariantLogisticsProfile.Create(Tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), changes ?? Changes(), Now);

    [Fact]
    public void TheDefaultProfileIsUndNoneWithConversionsFromPackagingUnits()
    {
        var profile = VariantLogisticsProfile.CreateDefault(Tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), [12, 144], Now);

        Assert.Equal("UND", profile.BaseUom.Value);
        Assert.Equal(0, profile.BaseUomPrecision);
        Assert.Equal(TrackingPolicy.None, profile.Tracking);
        Assert.Equal(
            new[] { ("EMPAQUE x12", 12m), ("EMPAQUE x144", 144m) },
            profile.Conversions.Select(c => (c.Code, c.Factor)).ToArray());
    }

    [Fact]
    public void ChangingTrackingWithStockRequiresIdentification()
    {
        var profile = NewProfile();

        var error = Assert.Throws<WmsDomainException>(() => profile.Update(Changes(TrackingPolicy.Lot), true, Now));

        Assert.Equal(WmsErrorCodes.ProfileTrackingChangeRequiresIdentification, error.Code);
        Assert.Equal(TrackingPolicy.None, profile.Tracking);
    }

    [Fact]
    public void ChangingTrackingWithoutStockIsAllowed()
    {
        var profile = NewProfile();
        var before = profile.Version;

        profile.Update(Changes(TrackingPolicy.Serial), false, Now.AddHours(1));

        Assert.Equal(TrackingPolicy.Serial, profile.Tracking);
        Assert.Equal(before + 1, profile.Version);
    }

    [Fact]
    public void KeepingTheTrackingWithStockIsAllowed()
    {
        var profile = NewProfile();

        profile.Update(Changes(weightKg: 2m), true, Now);

        Assert.Equal(2m, profile.WeightKg);
    }

    // I4 (pendiente de confirmación del owner): el saldo se guarda en la unidad base, así que con saldo cambiar la unidad,
    // bajar la precisión o cambiar el factor de una presentación existente reinterpreta los saldos sin avisar.
    [Theory]
    [InlineData("baseUom")]
    [InlineData("precisionDown")]
    [InlineData("existingFactor")]
    [InlineData("existingFactorCaseInsensitive")]
    public void WithStockChangingTheBaseUomOrAnExistingFactorIsRejected(string change)
    {
        var profile = VariantLogisticsProfile.Create(
            Tenant,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new ProfileChanges(UomCode.Parse("KG"), 3, TrackingPolicy.None, false, null, null, null, null, null, null, null, [("Caja", 6m, null)]),
            Now);

        var changes = change switch
        {
            "baseUom" => new ProfileChanges(UomCode.Parse("UND"), 3, TrackingPolicy.None, false, null, null, null, null, null, null, null, [("Caja", 6m, null)]),
            "precisionDown" => new ProfileChanges(UomCode.Parse("KG"), 2, TrackingPolicy.None, false, null, null, null, null, null, null, null, [("Caja", 6m, null)]),
            "existingFactor" => new ProfileChanges(UomCode.Parse("KG"), 3, TrackingPolicy.None, false, null, null, null, null, null, null, null, [("Caja", 12m, null)]),
            _ => new ProfileChanges(UomCode.Parse("KG"), 3, TrackingPolicy.None, false, null, null, null, null, null, null, null, [("CAJA", 12m, null)]),
        };

        var error = Assert.Throws<WmsDomainException>(() => profile.Update(changes, true, Now));

        Assert.Equal(WmsErrorCodes.ProfileBaseUomChangeRequiresIdentification, error.Code);
        Assert.Equal("KG", profile.BaseUom.Value);
        Assert.Equal(3, profile.BaseUomPrecision);
        Assert.Equal(6m, profile.Conversions.Single().Factor);
    }

    [Fact]
    public void WithStockANewRemovedOrRebarcodedConversionAndRaisedPrecisionPass()
    {
        var profile = VariantLogisticsProfile.Create(
            Tenant,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            new ProfileChanges(UomCode.Parse("KG"), 3, TrackingPolicy.None, false, null, null, null, null, null, null, null, [("Caja", 6m, null), ("Bulto", 20m, null)]),
            Now);

        // Caja conserva el factor y gana código de barras; Bulto se quita; Pallet es nueva; la precisión sube.
        profile.Update(
            new ProfileChanges(UomCode.Parse("KG"), 4, TrackingPolicy.None, false, null, null, null, null, null, null, null, [("Caja", 6m, "7701"), ("Pallet", 400m, null)]),
            true,
            Now.AddHours(1));

        Assert.Equal(4, profile.BaseUomPrecision);
        Assert.Equal(["Caja", "Pallet"], profile.Conversions.Select(c => c.Code));
    }

    [Fact]
    public void DuplicateConversionCodesAreRejected()
    {
        var error = Assert.Throws<WmsDomainException>(() =>
            NewProfile(Changes(conversions: [("caja", 6m, null), ("CAJA", 12m, null)])));

        Assert.Equal(WmsErrorCodes.ProfileConversionDuplicate, error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ANonPositiveFactorIsRejected(int factor)
    {
        var error = Assert.Throws<WmsDomainException>(() =>
            NewProfile(Changes(conversions: [("CAJA", factor, null)])));

        Assert.Equal(WmsErrorCodes.ProfileConversionFactorInvalid, error.Code);
    }

    [Fact]
    public void NegativeMeasuresAreRejected()
    {
        var error = Assert.Throws<WmsDomainException>(() => NewProfile(Changes(weightKg: -1m)));

        Assert.Equal(WmsErrorCodes.ProfileMeasureInvalid, error.Code);
    }

    [Fact]
    public void ThePrecisionOutsideZeroToSixIsRejected()
    {
        var bad = Changes() with { BaseUomPrecision = 7 };

        var error = Assert.Throws<WmsDomainException>(() => NewProfile(bad));

        Assert.Equal(WmsErrorCodes.UomPrecisionInvalid, error.Code);
    }

    [Fact]
    public void UpdatingKeepsTheIdOfAConversionThatKeepsItsCode()
    {
        var profile = NewProfile(Changes(conversions: [("CAJA", 6m, null)]));
        var id = profile.Conversions.Single().Id;

        profile.Update(Changes(conversions: [("caja", 12m, "770")]), false, Now);

        var conversion = Assert.Single(profile.Conversions);
        Assert.Equal(id, conversion.Id);
        Assert.Equal(12m, conversion.Factor);
        Assert.Equal("770", conversion.Barcode);
    }

    [Fact]
    public void ForVariantBuildsTheInternalAndPrimaryIdentifiers()
    {
        var variantId = Guid.CreateVersion7();

        var identifiers = Identifier.ForVariant(Tenant, variantId, "SKU-1", "7701234567890", Now);

        Assert.Equal(2, identifiers.Count);
        var internalOne = Assert.Single(identifiers, i => i.Symbology == IdentifierSymbology.Internal);
        Assert.Equal("SKU-1", internalOne.Value);
        Assert.Equal(IdentifierKind.Variant, internalOne.Kind);
        Assert.Equal(variantId, internalOne.TargetId);
        Assert.False(internalOne.IsPrimary);

        var barcode = Assert.Single(identifiers, i => i.Symbology == IdentifierSymbology.Gtin);
        Assert.Equal("7701234567890", barcode.Value);
        Assert.Equal(IdentifierKind.Variant, barcode.Kind);
        Assert.True(barcode.IsPrimary);

        Assert.Single(Identifier.ForVariant(Tenant, variantId, "SKU-1", null, Now));
    }

    [Fact]
    public void ForVariantWritesASingleRowWhenTheCodeEqualsTheBarcode()
    {
        // SKU = EAN es válido en Catalog; dos filas con el mismo valor chocarían con IX_identifiers_tenant_value contra sí mismas.
        var identifiers = Identifier.ForVariant(Tenant, Guid.CreateVersion7(), "7701234567890", "7701234567890", Now);

        var only = Assert.Single(identifiers);
        Assert.Equal(IdentifierSymbology.Gtin, only.Symbology);
        Assert.True(only.IsPrimary);
        Assert.Equal("7701234567890", only.Value);
    }

    [Fact]
    public void ForVariantComparesTheTrimmedValues()
    {
        Assert.Single(Identifier.ForVariant(Tenant, Guid.CreateVersion7(), " 770 ", "770", Now));
    }

    [Fact]
    public void AnEmptyIdentifierValueIsRejected()
    {
        var error = Assert.Throws<WmsDomainException>(() =>
            Identifier.Create(Guid.CreateVersion7(), Tenant, IdentifierKind.Lot, IdentifierSymbology.Code128, "  ", Guid.CreateVersion7(), null, false, Now));

        Assert.Equal(WmsErrorCodes.IdentifierValueInvalid, error.Code);
    }
}
