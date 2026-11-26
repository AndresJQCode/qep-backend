namespace Modules.Wms.Domain.Warehousing;

/// <summary>
/// Un nodo del árbol de la bodega. Los setters son privados y la mutación pasa por <see cref="Warehouse"/>: solo el agregado ve
/// el árbol completo, que es lo que hace falta para validar padres, códigos repetidos y rutas.
/// </summary>
public sealed class Location
{
    private readonly List<string> _storageClasses = [];

    private Location()
    {
    }

    public LocationId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public WarehouseId WarehouseId { get; private set; }

    public LocationId? ParentId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    /// <summary>Ruta desnormalizada (<c>BOG/A/01/B1</c>): las búsquedas y el recorrido de picking la leen; <c>Warehouse</c> la
    /// reescribe en todos los descendientes cuando un nodo se mueve o cambia de código.</summary>
    public string Path { get; private set; } = string.Empty;

    public LocationKind Kind { get; private set; }

    public LocationRole Role { get; private set; }

    public bool IsVirtual { get; private set; }

    public bool AllowsStorage { get; private set; }

    public bool AllowsPicking { get; private set; }

    public LocationCapacity Capacity { get; private set; }

    public int PutawayPriority { get; private set; }

    public IReadOnlyList<string> StorageClasses => _storageClasses;

    public LocationStatus Status { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>S1: lo que WMS-D copia a <c>stock_balances.allows_negative</c>. Calculada, sin columna. <c>TRANSIT</c> es virtual
    /// y aun así no admite negativo: el tránsito no puede deber lo que no salió.</summary>
    public bool AllowsNegative => Role is LocationRole.Supplier or LocationRole.Customer or LocationRole.Adjustment;

    internal static Location Create(
        LocationId id,
        Guid tenantId,
        WarehouseId warehouseId,
        LocationId? parentId,
        string code,
        string path,
        LocationKind kind,
        LocationRole role,
        bool isVirtual,
        bool allowsStorage,
        bool allowsPicking,
        LocationCapacity capacity,
        int putawayPriority,
        IReadOnlyList<string> storageClasses,
        DateTimeOffset now)
    {
        var location = new Location
        {
            Id = id,
            TenantId = tenantId,
            WarehouseId = warehouseId,
            ParentId = parentId,
            Code = code,
            Path = path,
            Kind = kind,
            Role = role,
            IsVirtual = isVirtual,
            AllowsStorage = allowsStorage,
            AllowsPicking = allowsPicking,
            Capacity = capacity,
            PutawayPriority = putawayPriority,
            Status = LocationStatus.Active,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        location._storageClasses.AddRange(storageClasses);
        return location;
    }

    internal void Apply(
        LocationId? parentId,
        string code,
        LocationKind kind,
        LocationRole role,
        bool allowsStorage,
        bool allowsPicking,
        LocationCapacity capacity,
        int putawayPriority,
        IReadOnlyList<string> storageClasses,
        LocationStatus status,
        DateTimeOffset now)
    {
        // Se copia antes de limpiar: si el llamador pasa la propia lista del nodo, Clear() la vaciaría.
        var classes = storageClasses.ToList();

        ParentId = parentId;
        Code = code;
        Kind = kind;
        Role = role;
        AllowsStorage = allowsStorage;
        AllowsPicking = allowsPicking;
        Capacity = capacity;
        PutawayPriority = putawayPriority;
        _storageClasses.Clear();
        _storageClasses.AddRange(classes);
        Status = status;
        Version++;
        UpdatedAt = now;
    }

    internal void Repath(string path) => Path = path;
}
