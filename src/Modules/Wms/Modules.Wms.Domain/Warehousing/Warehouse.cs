namespace Modules.Wms.Domain.Warehousing;

/// <summary>
/// Una bodega y su árbol de ubicaciones (spec WMS §5.2, §8). Nace con las cuatro virtuales y la zona de recepción con su
/// muelle; el agregado carga la bodega entera porque las tablas son chicas (§11.2) y las reglas del árbol necesitan verlo todo.
/// </summary>
public sealed class Warehouse
{
    public const int CodeMaxLength = 32;
    public const int NameMaxLength = 120;

    private readonly List<Location> _locations = [];

    private Warehouse()
    {
    }

    public WarehouseId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public Guid? CityId { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Inventario básico (§5.3): la bodega donde las compras suman y las ventas descuentan. Una por tenant; la base lo
    /// exige con un índice único parcial.</summary>
    public bool IsPrincipal { get; private set; }

    /// <summary>La ubicación donde cae el stock del inventario básico. Se guarda por id y no por código: el código se puede cambiar.
    /// Referencia blanda (sin FK): <see cref="EnsureGeneralLocation"/> la recrea si ya no está.</summary>
    public LocationId? GeneralLocationId { get; private set; }

    /// <summary>La zona de recepción que nace con la bodega. Se guarda por id y no por código: el código se puede cambiar y la
    /// protección contra el borrado (G-11) no puede depender de un texto editable. Referencia blanda, sin FK; es nula en las
    /// bodegas creadas antes de que existiera.</summary>
    public LocationId? ReceivingZoneId { get; private set; }

    /// <summary>El muelle de recepción (<c>REC-01</c> al nacer). Se guarda por id por la misma razón que
    /// <see cref="ReceivingZoneId"/>; la recepción lo busca por aquí y no por su código.</summary>
    public LocationId? ReceivingDockId { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<Location> Locations => _locations;

    public static Warehouse Create(WarehouseId id, Guid tenantId, string code, string name, Guid? cityId, DateTimeOffset now)
    {
        var warehouse = new Warehouse
        {
            Id = id,
            TenantId = tenantId,
            Code = LocationCodes.Normalize(code, WmsErrorCodes.WarehouseCodeInvalid),
            Name = NormalizeName(name),
            CityId = cityId,
            IsActive = true,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        warehouse.AddVirtual(LocationCodes.Supplier, LocationRole.Supplier, now);
        warehouse.AddVirtual(LocationCodes.Customer, LocationRole.Customer, now);
        warehouse.AddVirtual(LocationCodes.Adjustment, LocationRole.Adjustment, now);
        warehouse.AddVirtual(LocationCodes.Transit, LocationRole.Transit, now);

        var receiving = warehouse.Insert(
            LocationId.New(), null, LocationCodes.ReceivingZone, LocationKind.Zone, LocationRole.Receiving,
            isVirtual: false, allowsStorage: false, allowsPicking: false, LocationCapacity.None, 0, [], now);
        var dock = warehouse.Insert(
            LocationId.New(), receiving.Id, LocationCodes.ReceivingDock, LocationKind.Dock, LocationRole.Receiving,
            isVirtual: false, allowsStorage: true, allowsPicking: false, LocationCapacity.None, 0, [], now);
        warehouse.ReceivingZoneId = receiving.Id;
        warehouse.ReceivingDockId = dock.Id;

        return warehouse;
    }

    public void Update(string name, Guid? cityId, bool isActive, DateTimeOffset now)
    {
        // Sin principal activa el inventario básico no tendría dónde asentar: primero se elige otra.
        if (IsPrincipal && !isActive)
        {
            throw new WmsDomainException(
                WmsErrorCodes.WarehousePrincipalCannotBeDeactivated, "The principal warehouse cannot be deactivated; choose another one first.");
        }

        Name = NormalizeName(name);
        CityId = cityId;
        IsActive = isActive;
        Version++;
        UpdatedAt = now;
    }

    /// <summary>Marca esta bodega como la principal. El llamador quita antes la marca de la anterior y guarda: el índice único no es
    /// diferible.</summary>
    public void MarkPrincipal(DateTimeOffset now)
    {
        // Decisión del owner, 2026-11-27: una bodega inactiva no puede ser la principal (el inventario básico asienta ahí).
        if (!IsActive)
        {
            throw new WmsDomainException(
                WmsErrorCodes.WarehouseInactiveCannotBePrincipal, "Una bodega inactiva no puede ser la principal: actívala primero.");
        }

        IsPrincipal = true;
        Version++;
        UpdatedAt = now;
    }

    public void ClearPrincipal(DateTimeOffset now)
    {
        IsPrincipal = false;
        Version++;
        UpdatedAt = now;
    }

    /// <summary>La ubicación general del inventario básico: un <c>Bin</c> en la raíz, rol <c>Storage</c>, con almacenamiento y picking
    /// permitidos (sin picking, <c>pickable</c> sería 0 y la salida no podría asignar de ahí). Idempotente: si ya existe, la devuelve.
    /// Toma el primer código libre entre <c>GENERAL</c>, <c>GENERAL-1</c>, <c>GENERAL-2</c>…; <c>REC-01</c> no sirve porque la
    /// recepción la usa como muelle de llegada.</summary>
    public Location EnsureGeneralLocation(DateTimeOffset now)
    {
        if (GeneralLocationId is { } existingId && FindLocation(existingId) is { } existing)
        {
            return existing;
        }

        var code = LocationCodes.General;
        for (var suffix = 1; _locations.Exists(location => string.Equals(location.Code, code, StringComparison.Ordinal)); suffix++)
        {
            code = $"{LocationCodes.General}-{suffix}";
        }

        // Insert y no AddLocation: un Bin en la raíz no pasa la validación del árbol (sólo una Zone o un Dock cuelgan de la raíz).
        var general = Insert(
            LocationId.New(), null, code, LocationKind.Bin, LocationRole.Storage,
            isVirtual: false, allowsStorage: true, allowsPicking: true, LocationCapacity.None, 0, [], now);
        GeneralLocationId = general.Id;
        Version++;
        UpdatedAt = now;
        return general;
    }

    public Location AddLocation(
        LocationId id,
        LocationId? parentId,
        string code,
        LocationKind kind,
        LocationRole role,
        bool allowsStorage,
        bool allowsPicking,
        LocationCapacity capacity,
        int putawayPriority,
        IReadOnlyList<string> storageClasses,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(storageClasses);

        var normalized = ValidateLocation(null, parentId, code, kind, role, capacity, putawayPriority);
        return Insert(id, parentId, normalized, kind, role, false, allowsStorage, allowsPicking, capacity, putawayPriority, storageClasses, now);
    }

    public void UpdateLocation(
        LocationId id,
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
        ArgumentNullException.ThrowIfNull(storageClasses);

        var location = FindLocation(id) ?? throw new InvalidOperationException($"Location {id.Value} does not belong to warehouse {Id.Value}.");

        // S22: antes de comparar nada. Un PUT con el cuerpo idéntico también se rechaza; si no, el cliente no sabría si
        // "no cambió nada" es un éxito o un descuido.
        if (location.IsVirtual)
        {
            throw new WmsDomainException(WmsErrorCodes.LocationVirtualImmutable, "A virtual location cannot be edited.");
        }

        var normalized = ValidateLocation(location, parentId, code, kind, role, capacity, putawayPriority);
        var repath = location.ParentId != parentId || !string.Equals(location.Code, normalized, StringComparison.Ordinal);

        location.Apply(parentId, normalized, kind, role, allowsStorage, allowsPicking, capacity, putawayPriority, storageClasses, status, now);

        if (repath)
        {
            RebuildPaths(location);
        }
    }

    public void RemoveLocation(LocationId id)
    {
        var location = FindLocation(id) ?? throw new InvalidOperationException($"Location {id.Value} does not belong to warehouse {Id.Value}.");

        if (location.IsVirtual)
        {
            throw new WmsDomainException(WmsErrorCodes.LocationVirtualImmutable, "A virtual location cannot be removed.");
        }

        // RF-003: la zona de recepción y su muelle nacen con la bodega y se tratan como las virtuales; si se pudieran borrar, la
        // recepción quedaría sin dónde descargar. G-11: se protegen por identidad y no por código, porque el código se puede
        // editar y una ubicación nueva llamada REC-01 no debe quedar protegida por accidente.
        if (location.Id == ReceivingZoneId || location.Id == ReceivingDockId)
        {
            throw new WmsDomainException(WmsErrorCodes.LocationVirtualImmutable, "The receiving zone and its dock cannot be removed.");
        }

        if (_locations.Any(l => l.ParentId == id))
        {
            throw new WmsDomainException(WmsErrorCodes.LocationHasChildren, "The location has children and cannot be removed.");
        }

        _locations.Remove(location);
    }

    public Location VirtualLocation(LocationRole role) =>
        _locations.FirstOrDefault(l => l.IsVirtual && l.Role == role)
        ?? throw new InvalidOperationException($"Role {role} has no virtual location.");

    public Location? FindLocation(LocationId id) => _locations.FirstOrDefault(l => l.Id == id);

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trimmed.Length, NameMaxLength);
        return trimmed;
    }

    private static int Rank(LocationKind kind) => kind switch
    {
        LocationKind.Zone => 0,
        LocationKind.Aisle => 1,
        LocationKind.Rack => 2,
        LocationKind.Level => 3,
        LocationKind.Bin => 4,
        _ => -1,
    };

    private void AddVirtual(string code, LocationRole role, DateTimeOffset now) =>
        Insert(LocationId.New(), null, code, LocationKind.Virtual, role, true, false, false, LocationCapacity.None, 0, [], now);

    private Location Insert(
        LocationId id,
        LocationId? parentId,
        string code,
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
        var parent = parentId is { } p ? FindLocation(p) : null;
        var path = parent is null ? $"{Code}/{code}" : $"{parent.Path}/{code}";
        var location = Location.Create(
            id, TenantId, Id, parentId, code, path, kind, role, isVirtual, allowsStorage, allowsPicking, capacity,
            putawayPriority, storageClasses, now);
        _locations.Add(location);
        return location;
    }

    /// <summary>Valida las reglas de alta y edición (P-5) y devuelve el código normalizado. <paramref name="current"/> es el nodo
    /// que se edita, o <c>null</c> en un alta.</summary>
    private string ValidateLocation(
        Location? current,
        LocationId? parentId,
        string code,
        LocationKind kind,
        LocationRole role,
        LocationCapacity capacity,
        int putawayPriority)
    {
        if (kind == LocationKind.Virtual)
        {
            throw new WmsDomainException(WmsErrorCodes.LocationKindNotAllowed, "The Virtual kind cannot be set through the API.");
        }

        if (LocationCodes.IsVirtualRole(role))
        {
            throw new WmsDomainException(WmsErrorCodes.LocationRoleNotAllowed, "The role is reserved for the virtual locations.");
        }

        var normalized = LocationCodes.Normalize(code, WmsErrorCodes.LocationCodeInvalid);
        if (_locations.Any(l => l != current && string.Equals(l.Code, normalized, StringComparison.Ordinal)))
        {
            throw new WmsDomainException(WmsErrorCodes.LocationCodeTaken, "The location code is already used in this warehouse.");
        }

        if (putawayPriority < 0 || capacity.MaxWeightKg < 0 || capacity.MaxVolumeM3 < 0 || capacity.MaxUnits < 0)
        {
            throw new WmsDomainException(WmsErrorCodes.LocationCapacityInvalid, "Capacity and putaway priority cannot be negative.");
        }

        ValidateParent(current, parentId, kind);
        if (current is not null)
        {
            ValidateChildren(current, kind);
        }

        return normalized;
    }

    /// <summary>Cambiar el <c>kind</c> de un nodo con hijos no puede dejar un Bin o un Dock con hijos, ni un hijo que no rankee
    /// por debajo de su padre; sin esto, un Aisle con Racks se volvería Level por un simple PUT.</summary>
    private void ValidateChildren(Location current, LocationKind kind)
    {
        var children = _locations.Where(l => l.ParentId == current.Id).ToList();
        if (children.Count == 0)
        {
            return;
        }

        if (kind is LocationKind.Bin or LocationKind.Dock)
        {
            throw ParentInvalid("A Bin or Dock cannot have children.");
        }

        // Un Dock hijo solo vive bajo una Zone; los demás deben rankear estrictamente por debajo.
        if (children.Any(c => c.Kind == LocationKind.Dock ? kind != LocationKind.Zone : Rank(c.Kind) <= Rank(kind)))
        {
            throw ParentInvalid("A child must rank below its parent: Zone, Aisle, Rack, Level, Bin.");
        }
    }

    private void ValidateParent(Location? current, LocationId? parentId, LocationKind kind)
    {
        Location? parent = null;
        if (parentId is { } p)
        {
            parent = FindLocation(p);
            if (parent is null || (current is not null && (parent == current || IsDescendantOf(parent, current))))
            {
                throw ParentInvalid("The parent does not exist or would create a cycle.");
            }

            // Un Bin es la hoja del árbol y un muelle o una virtual no cuelgan hijos.
            if (parent.Kind is LocationKind.Bin or LocationKind.Dock or LocationKind.Virtual)
            {
                throw ParentInvalid("A Bin, Dock or virtual location cannot have children.");
            }
        }

        if (kind == LocationKind.Dock)
        {
            // REC-01 nace colgado de la zona de recepción, así que el muelle admite raíz o una Zone (P-5 lo dejaba sólo en la raíz,
            // lo que habría vuelto inmodificable al muelle que la propia bodega crea).
            if (parent is not null && parent.Kind != LocationKind.Zone)
            {
                throw ParentInvalid("A Dock can only be at the root or under a Zone.");
            }

            return;
        }

        if (parent is null)
        {
            if (kind != LocationKind.Zone)
            {
                throw ParentInvalid("Only a Zone or a Dock can be at the root.");
            }

            return;
        }

        if (Rank(parent.Kind) >= Rank(kind))
        {
            throw ParentInvalid("A child must rank below its parent: Zone, Aisle, Rack, Level, Bin.");
        }
    }

    private static WmsDomainException ParentInvalid(string message) => new(WmsErrorCodes.LocationParentInvalid, message);

    private bool IsDescendantOf(Location candidate, Location ancestor)
    {
        var cursor = candidate;
        while (cursor.ParentId is { } parentId)
        {
            if (parentId == ancestor.Id)
            {
                return true;
            }

            cursor = FindLocation(parentId)!;
        }

        return false;
    }

    /// <summary>Recorre desde el nodo cambiado hacia abajo (BFS por <c>ParentId</c>) y reescribe cada <c>Path</c>.</summary>
    private void RebuildPaths(Location root)
    {
        var parent = root.ParentId is { } p ? FindLocation(p) : null;
        root.Repath(parent is null ? $"{Code}/{root.Code}" : $"{parent.Path}/{root.Code}");

        var queue = new Queue<Location>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var child in _locations.Where(l => l.ParentId == node.Id))
            {
                child.Repath($"{node.Path}/{child.Code}");
                queue.Enqueue(child);
            }
        }
    }
}
