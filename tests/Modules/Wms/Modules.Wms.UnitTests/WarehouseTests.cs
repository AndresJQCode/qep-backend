using Modules.Wms.Domain;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests;

public sealed class WarehouseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();

    internal static Warehouse NewWarehouse(string code = "BOG") =>
        Warehouse.Create(WarehouseId.New(), Tenant, code, "Bogotá", null, Now);

    [Fact]
    public void CreatingAWarehouseCreatesTheFourVirtualsAndTheReceivingDock()
    {
        var warehouse = NewWarehouse();

        Assert.Equal(6, warehouse.Locations.Count);
        foreach (var (code, role) in new[]
        {
            ("SUPPLIER", LocationRole.Supplier), ("CUSTOMER", LocationRole.Customer),
            ("ADJUSTMENT", LocationRole.Adjustment), ("TRANSIT", LocationRole.Transit),
        })
        {
            var virtualLocation = warehouse.VirtualLocation(role);
            Assert.Equal(code, virtualLocation.Code);
            Assert.True(virtualLocation.IsVirtual);
            Assert.Equal(LocationKind.Virtual, virtualLocation.Kind);
            Assert.False(virtualLocation.AllowsStorage);
            Assert.False(virtualLocation.AllowsPicking);
            Assert.Null(virtualLocation.ParentId);
            Assert.Equal($"BOG/{code}", virtualLocation.Path);
        }

        var zone = Assert.Single(warehouse.Locations, l => l.Code == "RECEIVING");
        Assert.Equal(LocationKind.Zone, zone.Kind);
        Assert.Equal(LocationRole.Receiving, zone.Role);
        Assert.False(zone.IsVirtual);
        Assert.Equal("BOG/RECEIVING", zone.Path);

        var dock = Assert.Single(warehouse.Locations, l => l.Code == "REC-01");
        Assert.Equal(LocationKind.Dock, dock.Kind);
        Assert.Equal(LocationRole.Receiving, dock.Role);
        Assert.Equal(zone.Id, dock.ParentId);
        Assert.Equal("BOG/RECEIVING/REC-01", dock.Path);
        Assert.True(dock.AllowsStorage);
    }

    [Fact]
    public void TheCodeIsNormalizedToUpperCaseAndValidated()
    {
        Assert.Equal("BOG", NewWarehouse(" bog ").Code);

        var error = Assert.Throws<WmsDomainException>(() => NewWarehouse("bo g"));
        Assert.Equal(WmsErrorCodes.WarehouseCodeInvalid, error.Code);
    }

    [Fact]
    public void RemovingAVirtualLocationIsRejected()
    {
        var warehouse = NewWarehouse();

        var error = Assert.Throws<WmsDomainException>(() => warehouse.RemoveLocation(warehouse.VirtualLocation(LocationRole.Transit).Id));

        Assert.Equal(WmsErrorCodes.LocationVirtualImmutable, error.Code);
    }

    [Theory]
    [InlineData("RECEIVING")]
    [InlineData("REC-01")]
    public void RemovingTheReceivingZoneOrItsDockIsRejectedAsImmutable(string code)
    {
        var warehouse = NewWarehouse();
        var location = warehouse.Locations.Single(l => l.Code == code);

        var error = Assert.Throws<WmsDomainException>(() => warehouse.RemoveLocation(location.Id));

        // RF-003: la zona y el muelle de recepción nacen con la bodega y no se borran, igual que las virtuales.
        Assert.Equal(WmsErrorCodes.LocationVirtualImmutable, error.Code);
        Assert.NotNull(warehouse.FindLocation(location.Id));
    }

    [Fact]
    public void RemovingAnEmptyBinWorks()
    {
        var warehouse = NewWarehouse();
        var zone = warehouse.AddLocation(LocationId.New(), null, "A", LocationKind.Zone, LocationRole.Storage, false, false, LocationCapacity.None, 0, [], Now);
        var bin = warehouse.AddLocation(LocationId.New(), zone.Id, "B1", LocationKind.Bin, LocationRole.Storage, true, true, LocationCapacity.None, 0, [], Now);

        warehouse.RemoveLocation(bin.Id);

        Assert.Null(warehouse.FindLocation(bin.Id));
        Assert.NotNull(warehouse.FindLocation(zone.Id));
    }

    [Fact]
    public void UpdateBumpsTheVersion()
    {
        var warehouse = NewWarehouse();
        var before = warehouse.Version;
        var cityId = Guid.CreateVersion7();

        warehouse.Update("Bogotá norte", cityId, false, Now.AddHours(1));

        Assert.Equal(before + 1, warehouse.Version);
        Assert.Equal("Bogotá norte", warehouse.Name);
        Assert.Equal(cityId, warehouse.CityId);
        Assert.False(warehouse.IsActive);
        Assert.Equal(Now.AddHours(1), warehouse.UpdatedAt);
    }

    [Fact]
    public void MarkingPrincipalSetsTheFlag()
    {
        var warehouse = NewWarehouse();
        Assert.False(warehouse.IsPrincipal);
        var before = warehouse.Version;

        warehouse.MarkPrincipal(Now.AddHours(1));

        Assert.True(warehouse.IsPrincipal);
        Assert.Equal(before + 1, warehouse.Version);
        Assert.Equal(Now.AddHours(1), warehouse.UpdatedAt);

        warehouse.ClearPrincipal(Now.AddHours(2));

        Assert.False(warehouse.IsPrincipal);
        Assert.Equal(before + 2, warehouse.Version);
    }

    [Fact]
    public void AnInactiveWarehouseCannotBecomePrincipal()
    {
        var warehouse = NewWarehouse();
        warehouse.Update("Bogotá", null, false, Now);
        var version = warehouse.Version;

        var error = Assert.Throws<WmsDomainException>(() => warehouse.MarkPrincipal(Now));

        Assert.Equal("wms.warehouse.inactive_cannot_be_principal", error.Code);
        Assert.Equal(WmsErrorCodes.WarehouseInactiveCannotBePrincipal, error.Code);
        Assert.False(warehouse.IsPrincipal);
        Assert.Equal(version, warehouse.Version);
    }

    [Fact]
    public void DeactivatingThePrincipalIsRejected()
    {
        var warehouse = NewWarehouse();
        warehouse.MarkPrincipal(Now);

        var error = Assert.Throws<WmsDomainException>(() => warehouse.Update("Bogotá", null, false, Now));

        Assert.Equal("wms.warehouse.principal_cannot_be_deactivated", error.Code);
        Assert.Equal(WmsErrorCodes.WarehousePrincipalCannotBeDeactivated, error.Code);
        Assert.True(warehouse.IsActive);
        // Seguir activa sí se puede: sólo desactivarla se rechaza.
        warehouse.Update("Bogotá norte", null, true, Now);
        Assert.Equal("Bogotá norte", warehouse.Name);
    }

    [Fact]
    public void EnsureGeneralLocationCreatesARootStorageBinThatAllowsPicking()
    {
        var warehouse = NewWarehouse();
        Assert.Null(warehouse.GeneralLocationId);

        var general = warehouse.EnsureGeneralLocation(Now);

        Assert.Equal("GENERAL", general.Code);
        Assert.Equal(LocationKind.Bin, general.Kind);
        Assert.Equal(LocationRole.Storage, general.Role);
        Assert.Null(general.ParentId);
        Assert.False(general.IsVirtual);
        Assert.True(general.AllowsStorage);
        Assert.True(general.AllowsPicking);
        Assert.Equal("BOG/GENERAL", general.Path);
        Assert.Equal(general.Id, warehouse.GeneralLocationId);
        Assert.Same(general, warehouse.FindLocation(general.Id));
    }

    [Fact]
    public void EnsureGeneralLocationTakesTheNextFreeCode()
    {
        var warehouse = NewWarehouse();
        warehouse.AddLocation(LocationId.New(), null, "GENERAL", LocationKind.Zone, LocationRole.Storage, false, false, LocationCapacity.None, 0, [], Now);

        Assert.Equal("GENERAL-1", warehouse.EnsureGeneralLocation(Now).Code);

        var other = NewWarehouse("MED");
        other.AddLocation(LocationId.New(), null, "GENERAL", LocationKind.Zone, LocationRole.Storage, false, false, LocationCapacity.None, 0, [], Now);
        other.AddLocation(LocationId.New(), null, "GENERAL-1", LocationKind.Zone, LocationRole.Storage, false, false, LocationCapacity.None, 0, [], Now);
        Assert.Equal("GENERAL-2", other.EnsureGeneralLocation(Now).Code);
    }

    [Fact]
    public void EnsureGeneralLocationIsIdempotent()
    {
        var warehouse = NewWarehouse();

        var first = warehouse.EnsureGeneralLocation(Now);
        var count = warehouse.Locations.Count;
        var second = warehouse.EnsureGeneralLocation(Now.AddMinutes(1));

        Assert.Same(first, second);
        Assert.Equal(count, warehouse.Locations.Count);
    }
}
