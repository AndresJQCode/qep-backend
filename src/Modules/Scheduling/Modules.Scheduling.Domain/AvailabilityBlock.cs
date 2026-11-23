namespace Modules.Scheduling.Domain;

/// <summary>
/// La franja: lo que el tenant abre para reservar. Declara los servicios que se reservan en ella, los recursos que la atienden (uno o
/// varios, en orden) y el cupo <b>por recurso</b>: con tres recursos y cupo 1 caben tres citas a la misma hora, una por recurso. Se
/// repite por semana o es puntual, con vigencia opcional (spec 2026-11-21, «Franjas»). Los espacios libres no se guardan: los calcula el
/// motor de disponibilidad a partir de esto.
/// </summary>
public sealed class AvailabilityBlock : ArchivableEntity
{
    public const int MaxCapacity = 500;

    private AvailabilityBlock()
    {
    }

    public AvailabilityBlockId Id { get; private set; }

    public LocationId LocationId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public BlockKind Kind { get; private set; }

    public IReadOnlyList<WeekdayHours> Weekly { get; private set; } = [];

    public DateHours? OneOff { get; private set; }

    public DateOnly? ValidFrom { get; private set; }

    public DateOnly? ValidTo { get; private set; }

    public bool ClosesOnHolidays { get; private set; }

    public int CapacityPerResource { get; private set; }

    /// <summary>Sólo existe con cupo mayor que 1; con cupo 1 es <c>null</c>.</summary>
    public SessionMode? SessionMode { get; private set; }

    public bool AllowClientChoice { get; private set; }

    public AssignmentRule AssignmentRule { get; private set; }

    public IReadOnlyList<ServiceId> ServiceIds { get; private set; } = [];

    public IReadOnlyList<ResourceId> ResourceIds { get; private set; } = [];

    public static AvailabilityBlock Create(
        AvailabilityBlockId id, Guid tenantId, LocationId locationId, BlockDefinition definition, DateTimeOffset now)
    {
        var block = new AvailabilityBlock { Id = id, LocationId = locationId };
        block.Apply(definition);
        block.Initialize(tenantId, now);
        return block;
    }

    public void Update(BlockDefinition definition, DateTimeOffset now)
    {
        Apply(definition);
        Touch(now);
    }

    /// <summary>
    /// Archiving a resource takes it out of the block. The ids live in a column without a foreign key, so the block must forget them.
    /// A block left without resources is archived with it: an empty one is rejected on create. Returns whether anything changed.
    /// </summary>
    public bool DetachResource(ResourceId resource, DateTimeOffset now)
    {
        if (!ResourceIds.Contains(resource))
        {
            return false;
        }

        ResourceIds = [.. ResourceIds.Where(id => id != resource)];
        if (ResourceIds.Count == 0)
        {
            // Archive bumps the version itself.
            Archive(now);
        }
        else
        {
            Touch(now);
        }

        return true;
    }

    /// <summary>The same as <see cref="DetachResource"/> for an archived service.</summary>
    public bool DetachService(ServiceId service, DateTimeOffset now)
    {
        if (!ServiceIds.Contains(service))
        {
            return false;
        }

        ServiceIds = [.. ServiceIds.Where(id => id != service)];
        if (ServiceIds.Count == 0)
        {
            // Archive bumps the version itself.
            Archive(now);
        }
        else
        {
            Touch(now);
        }

        return true;
    }

    /// <summary>Los rangos que la franja abre ese día del calendario del tenant, sin descontar festivos, cierres ni citas (eso es del motor).</summary>
    public IReadOnlyList<TimeRange> RangesOn(DateOnly date)
    {
        if (Archived || (ValidFrom is { } from && date < from) || (ValidTo is { } to && date > to))
        {
            return [];
        }

        return Kind switch
        {
            BlockKind.OneOff => OneOff is { } window && window.Date == date ? window.Ranges : [],
            _ => Weekly.FirstOrDefault(day => day.Day == WeekdayHours.DayNumber(date))?.Ranges ?? [],
        };
    }

    private void Apply(BlockDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var name = RequireName(definition.Name);
        if (definition.CapacityPerResource < 1 || definition.CapacityPerResource > MaxCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(definition), definition.CapacityPerResource, "The capacity must be at least 1.");
        }

        if (definition.ValidFrom is { } from && definition.ValidTo is { } to && from > to)
        {
            throw new ArgumentException("The validity window cannot run backwards.", nameof(definition));
        }

        RequireDistinctAndNotEmpty(definition.ServiceIds, "service");
        RequireDistinctAndNotEmpty(definition.ResourceIds, "resource");
        RequireTheRepetition(definition);

        Name = name;
        Kind = definition.Kind;
        Weekly = definition.Kind == BlockKind.Weekly ? [.. definition.Weekly.OrderBy(day => day.Day)] : [];
        OneOff = definition.Kind == BlockKind.OneOff ? definition.OneOff : null;
        ValidFrom = definition.ValidFrom;
        ValidTo = definition.ValidTo;
        ClosesOnHolidays = definition.ClosesOnHolidays;
        CapacityPerResource = definition.CapacityPerResource;
        SessionMode = definition.CapacityPerResource > 1 ? definition.SessionMode ?? Domain.SessionMode.FirstBookingOpens : null;
        AllowClientChoice = definition.AllowClientChoice;
        AssignmentRule = definition.AssignmentRule;
        ServiceIds = [.. definition.ServiceIds];
        ResourceIds = [.. definition.ResourceIds];
    }

    private static void RequireTheRepetition(BlockDefinition definition)
    {
        if (definition.Kind == BlockKind.Weekly)
        {
            if (definition.Weekly.Count == 0 || definition.OneOff is not null
                || definition.Weekly.Select(day => day.Day).Distinct().Count() != definition.Weekly.Count)
            {
                throw new ArgumentException("A weekly block needs its days, once each, and no date.", nameof(definition));
            }

            return;
        }

        if (definition.OneOff is null || definition.Weekly.Count != 0)
        {
            throw new ArgumentException("A one-off block needs exactly one date and no weekly days.", nameof(definition));
        }
    }

    private static void RequireDistinctAndNotEmpty<T>(IReadOnlyList<T> values, string what)
    {
        if (values.Count == 0 || values.Distinct().Count() != values.Count)
        {
            throw new ArgumentException($"A block needs at least one {what}, without repeating any.", nameof(values));
        }
    }
}
