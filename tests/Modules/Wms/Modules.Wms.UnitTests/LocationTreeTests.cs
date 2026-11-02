using Modules.Wms.Domain;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests;

public sealed class LocationTreeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static Location Add(Warehouse w, LocationId? parent, string code, LocationKind kind, LocationRole role = LocationRole.Storage) =>
        w.AddLocation(LocationId.New(), parent, code, kind, role, true, true, LocationCapacity.None, 0, [], Now);

    private static void Move(Warehouse w, Location l, LocationId? parent, string? code = null) =>
        w.UpdateLocation(l.Id, parent, code ?? l.Code, l.Kind, l.Role, l.AllowsStorage, l.AllowsPicking, l.Capacity, l.PutawayPriority, l.StorageClasses, l.Status, Now);

    private static string Code(Action action) => Assert.Throws<WmsDomainException>(action).Code;

    [Fact]
    public void AZoneAisleRackLevelBinChainBuildsThePath()
    {
        var w = WarehouseTests.NewWarehouse();
        var zone = Add(w, null, "A", LocationKind.Zone);
        var aisle = Add(w, zone.Id, "01", LocationKind.Aisle);
        var rack = Add(w, aisle.Id, "02", LocationKind.Rack);
        var level = Add(w, rack.Id, "03", LocationKind.Level);
        var bin = Add(w, level.Id, "B1", LocationKind.Bin);

        Assert.Equal("BOG/A/01/02/03/B1", bin.Path);
    }

    [Fact]
    public void AZoneBinChainIsAllowed()
    {
        var w = WarehouseTests.NewWarehouse();
        var zone = Add(w, null, "A", LocationKind.Zone);

        var bin = Add(w, zone.Id, "B1", LocationKind.Bin);

        Assert.Equal("BOG/A/B1", bin.Path);
    }

    [Fact]
    public void AKindAboveItsParentIsRejected()
    {
        var w = WarehouseTests.NewWarehouse();
        var zone = Add(w, null, "A", LocationKind.Zone);
        var level = Add(w, zone.Id, "L1", LocationKind.Level);

        Assert.Equal(WmsErrorCodes.LocationParentInvalid, Code(() => Add(w, level.Id, "R1", LocationKind.Rack)));
        Assert.Equal(WmsErrorCodes.LocationParentInvalid, Code(() => Add(w, null, "R2", LocationKind.Rack)));
    }

    [Fact]
    public void ADockOrAVirtualCannotHaveChildren()
    {
        var w = WarehouseTests.NewWarehouse();
        var dock = w.Locations.Single(l => l.Code == "REC-01");
        var transit = w.VirtualLocation(LocationRole.Transit);

        Assert.Equal(WmsErrorCodes.LocationParentInvalid, Code(() => Add(w, dock.Id, "B1", LocationKind.Bin)));
        Assert.Equal(WmsErrorCodes.LocationParentInvalid, Code(() => Add(w, transit.Id, "B2", LocationKind.Bin)));
    }

    [Fact]
    public void AVirtualKindCannotBeCreatedThroughTheApi()
    {
        var w = WarehouseTests.NewWarehouse();

        Assert.Equal(WmsErrorCodes.LocationKindNotAllowed, Code(() => Add(w, null, "V1", LocationKind.Virtual)));
    }

    [Fact]
    public void AVirtualRoleCannotBeCreatedThroughTheApi()
    {
        var w = WarehouseTests.NewWarehouse();

        Assert.Equal(WmsErrorCodes.LocationRoleNotAllowed, Code(() => Add(w, null, "Z1", LocationKind.Zone, LocationRole.Transit)));
    }

    [Fact]
    public void ADuplicateCodeInTheWarehouseIsRejected()
    {
        var w = WarehouseTests.NewWarehouse();
        Add(w, null, "a-01", LocationKind.Zone);

        Assert.Equal(WmsErrorCodes.LocationCodeTaken, Code(() => Add(w, null, "A-01", LocationKind.Zone)));
        Assert.Equal(WmsErrorCodes.LocationCodeTaken, Code(() => Add(w, null, "supplier", LocationKind.Zone)));
    }

    [Fact]
    public void MovingANodeRewritesTheDescendantPaths()
    {
        var w = WarehouseTests.NewWarehouse();
        var zoneA = Add(w, null, "A", LocationKind.Zone);
        var zoneB = Add(w, null, "B", LocationKind.Zone);
        var rack = Add(w, zoneA.Id, "01", LocationKind.Rack);
        var level = Add(w, rack.Id, "02", LocationKind.Level);
        var bin = Add(w, level.Id, "B1", LocationKind.Bin);
        var versionBefore = bin.Version;

        Move(w, rack, zoneB.Id);

        Assert.Equal("BOG/B/01", rack.Path);
        Assert.Equal("BOG/B/01/02", level.Path);
        Assert.Equal("BOG/B/01/02/B1", bin.Path);
        Assert.Equal(versionBefore, bin.Version);
        Assert.Equal(2, rack.Version);
    }

    [Fact]
    public void RenamingAZoneRewritesTheDescendantPaths()
    {
        var w = WarehouseTests.NewWarehouse();
        var zone = Add(w, null, "A", LocationKind.Zone);
        var bin = Add(w, zone.Id, "B1", LocationKind.Bin);

        Move(w, zone, null, "norte");

        Assert.Equal("BOG/NORTE", zone.Path);
        Assert.Equal("BOG/NORTE/B1", bin.Path);
    }

    [Fact]
    public void MovingANodeUnderItsOwnDescendantIsRejected()
    {
        var w = WarehouseTests.NewWarehouse();
        var zone = Add(w, null, "A", LocationKind.Zone);
        var aisle = Add(w, zone.Id, "01", LocationKind.Aisle);
        var rack = Add(w, aisle.Id, "02", LocationKind.Rack);

        Assert.Equal(WmsErrorCodes.LocationParentInvalid, Code(() => Move(w, aisle, rack.Id)));
        Assert.Equal(WmsErrorCodes.LocationParentInvalid, Code(() => Move(w, aisle, aisle.Id)));
    }

    [Fact]
    public void UpdatingAVirtualLocationIsRejectedEvenWithoutChanges()
    {
        var w = WarehouseTests.NewWarehouse();
        var transit = w.VirtualLocation(LocationRole.Transit);

        var same = Code(() => w.UpdateLocation(
            transit.Id, transit.ParentId, transit.Code, transit.Kind, transit.Role, transit.AllowsStorage, transit.AllowsPicking,
            transit.Capacity, transit.PutawayPriority, transit.StorageClasses, transit.Status, Now));
        var blocked = Code(() => w.UpdateLocation(
            transit.Id, transit.ParentId, transit.Code, transit.Kind, transit.Role, transit.AllowsStorage, transit.AllowsPicking,
            transit.Capacity, transit.PutawayPriority, transit.StorageClasses, LocationStatus.Blocked, Now));

        Assert.Equal(WmsErrorCodes.LocationVirtualImmutable, same);
        Assert.Equal(WmsErrorCodes.LocationVirtualImmutable, blocked);
    }

    [Fact]
    public void OnlySupplierCustomerAndAdjustmentAllowNegative()
    {
        var w = WarehouseTests.NewWarehouse();
        var zone = Add(w, null, "A", LocationKind.Zone);
        var bin = Add(w, zone.Id, "B1", LocationKind.Bin);

        Assert.True(w.VirtualLocation(LocationRole.Supplier).AllowsNegative);
        Assert.True(w.VirtualLocation(LocationRole.Customer).AllowsNegative);
        Assert.True(w.VirtualLocation(LocationRole.Adjustment).AllowsNegative);
        Assert.False(w.VirtualLocation(LocationRole.Transit).AllowsNegative);
        Assert.False(w.Locations.Single(l => l.Code == "RECEIVING").AllowsNegative);
        Assert.False(w.Locations.Single(l => l.Code == "REC-01").AllowsNegative);
        Assert.False(bin.AllowsNegative);
    }

    [Fact]
    public void NegativeCapacityIsRejected()
    {
        var w = WarehouseTests.NewWarehouse();

        Assert.Equal(WmsErrorCodes.LocationCapacityInvalid, Code(() =>
            w.AddLocation(LocationId.New(), null, "A", LocationKind.Zone, LocationRole.Storage, false, false, new LocationCapacity(-1m, null, null), 0, [], Now)));
        Assert.Equal(WmsErrorCodes.LocationCapacityInvalid, Code(() =>
            w.AddLocation(LocationId.New(), null, "A", LocationKind.Zone, LocationRole.Storage, false, false, new LocationCapacity(null, null, -1), 0, [], Now)));
        Assert.Equal(WmsErrorCodes.LocationCapacityInvalid, Code(() =>
            w.AddLocation(LocationId.New(), null, "A", LocationKind.Zone, LocationRole.Storage, false, false, LocationCapacity.None, -1, [], Now)));
    }

    private static void ChangeKind(Warehouse w, Location l, LocationKind kind) =>
        w.UpdateLocation(l.Id, l.ParentId, l.Code, kind, l.Role, l.AllowsStorage, l.AllowsPicking, l.Capacity, l.PutawayPriority, l.StorageClasses, l.Status, Now);

    [Fact]
    public void ChangingAKindToBinWithChildrenIsRejected()
    {
        var w = WarehouseTests.NewWarehouse();
        var zone = Add(w, null, "A", LocationKind.Zone);
        var rack = Add(w, zone.Id, "R1", LocationKind.Rack);
        Add(w, rack.Id, "L1", LocationKind.Level);

        Assert.Equal(WmsErrorCodes.LocationParentInvalid, Code(() => ChangeKind(w, rack, LocationKind.Bin)));
        Assert.Equal(WmsErrorCodes.LocationParentInvalid, Code(() => ChangeKind(w, rack, LocationKind.Dock)));
        Assert.Equal(LocationKind.Rack, rack.Kind);
    }

    [Fact]
    public void ChangingAKindAtOrBelowAChildIsRejected()
    {
        var w = WarehouseTests.NewWarehouse();
        var zone = Add(w, null, "A", LocationKind.Zone);
        var aisle = Add(w, zone.Id, "01", LocationKind.Aisle);
        Add(w, aisle.Id, "R1", LocationKind.Rack);

        Assert.Equal(WmsErrorCodes.LocationParentInvalid, Code(() => ChangeKind(w, aisle, LocationKind.Level)));
        Assert.Equal(WmsErrorCodes.LocationParentInvalid, Code(() => ChangeKind(w, aisle, LocationKind.Rack)));

        // Un cambio que mantiene a los hijos por debajo sí pasa: Rack con un Bin puede volverse Level.
        var rack = Add(w, zone.Id, "R2", LocationKind.Rack);
        Add(w, rack.Id, "B1", LocationKind.Bin);
        ChangeKind(w, rack, LocationKind.Level);
        Assert.Equal(LocationKind.Level, rack.Kind);
    }

    [Fact]
    public void ADockUnderANonZoneIsRejected()
    {
        var w = WarehouseTests.NewWarehouse();
        var zone = Add(w, null, "A", LocationKind.Zone);
        var aisle = Add(w, zone.Id, "01", LocationKind.Aisle);

        Assert.Equal(WmsErrorCodes.LocationParentInvalid, Code(() => Add(w, aisle.Id, "D1", LocationKind.Dock)));
    }

    [Fact]
    public void TheReceivingDockCanBeUpdated()
    {
        var w = WarehouseTests.NewWarehouse();
        var dock = w.Locations.Single(l => l.Code == "REC-01");

        Move(w, dock, dock.ParentId, "muelle-1");

        Assert.Equal("BOG/RECEIVING/MUELLE-1", dock.Path);
        Assert.Equal(2, dock.Version);
    }
}
