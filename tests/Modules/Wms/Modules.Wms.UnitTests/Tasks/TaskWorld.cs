using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests.Tasks;

/// <summary>Una bodega con tres bins, una variante y dobles en memoria de los puertos que usan los casos de uso de tareas. Las
/// transacciones y los bloqueos no existen aquí: sólo se anota en <see cref="Log"/> en qué orden se pidieron. Lo que depende de la
/// base lo prueban las pruebas de integración.</summary>
internal sealed partial class TaskWorld
{
    public static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Member = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid OtherMember = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid Variant = Guid.Parse("66666666-6666-6666-6666-666666666666");
    public static readonly DateTimeOffset Start = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    public const string Device = "zebra-01";

    public TaskWorld()
    {
        Warehouse = Warehouse.Create(WarehouseId.New(), Tenant, "BOG", "Bogotá", null, Start);
        var zone = AddLocation(null, "A", LocationKind.Zone, allowsStorage: false);
        From = AddLocation(zone.Id, "A-01-01", LocationKind.Bin, allowsStorage: true).Id;
        To = AddLocation(zone.Id, "A-01-02", LocationKind.Bin, allowsStorage: true).Id;
        Spare = AddLocation(zone.Id, "A-01-03", LocationKind.Bin, allowsStorage: true).Id;
        NoStorage = AddLocation(zone.Id, "A-01-04", LocationKind.Bin, allowsStorage: false).Id;
        Owner = OwnerId.New();
    }

    public Warehouse Warehouse { get; }

    public LocationId From { get; }

    public LocationId To { get; }

    public LocationId Spare { get; }

    public LocationId NoStorage { get; }

    public OwnerId Owner { get; }

    public DateTimeOffset Now { get; set; } = Start;

    public List<WarehouseTask> Tasks { get; } = [];

    public Dictionary<Guid, string> Names { get; } = new() { [Member] = "Ana Operaria" };

    /// <summary>Qué se pidió y en qué orden: bloqueos, ledger, recibo y guardado.</summary>
    public List<string> Log { get; } = [];

    /// <summary>Las ubicaciones que se pidieron por código, una lista por llamada.</summary>
    public List<IReadOnlyCollection<LocationId>> LocationReads { get; } = [];

    public WarehouseTaskLine Line(decimal quantity = 5, LotId? lot = null) =>
        WarehouseTaskLine.Create(Variant, lot, StockStatus.Available, From, To, quantity);

    public WarehouseTask AddTask(bool publish = true, Guid? assignTo = null, params WarehouseTaskLine[] lines)
    {
        var task = WarehouseTask.CreateMove(
            Tenant, $"TSK-20261126-{Tasks.Count + 1:D4}", Warehouse.Id, Owner, null, null, null,
            lines.Length == 0 ? [Line()] : lines, assignTo, publish, OtherMember, Start);
        Tasks.Add(task);
        return task;
    }

    private Location AddLocation(LocationId? parent, string code, LocationKind kind, bool allowsStorage) =>
        Warehouse.AddLocation(
            LocationId.New(), parent, code, kind, LocationRole.Storage, allowsStorage, false, LocationCapacity.None, 0, [], Start);

    public WarehouseTaskDtoComposer Composer() =>
        new(new FakeWarehouses(this), new FakeCatalog(), new FakeLots(this), new FakeProfiles(), new FakeMembers(this), new FakeClock(this));
}
