using System.Text.Json;

namespace Modules.Support.Domain;

/// <summary>
/// Un ticket de soporte (spec 2026-11-14). Copia del tipo el nombre, el término y si es legal, así que editar el tipo después no
/// mueve el ticket. El plazo cuenta en días hábiles desde el día siguiente a la fecha local de radicación (§7.2): se detiene
/// mientras el estado lo detiene (<see cref="SupportTicketStatuses.StopsTheClock"/>) y al reanudar el vencimiento se corre por los
/// días hábiles estrictamente entre la fecha de la pausa y la de la reanudación (plan D3). Toda mutación real agrega un evento con
/// el ordinal siguiente y sube <see cref="Version"/>; un no-op no hace ninguna de las dos.
/// </summary>
public sealed class SupportTicket
{
    public const int SubjectMaxLength = 200;

    public const int DescriptionMaxLength = 5000;

    private readonly List<SupportTicketEvent> events = [];

    // EF Core materializa por acá; CreateManual es el único punto de entrada de código.
    private SupportTicket()
    {
        TypeName = string.Empty;
        Subject = string.Empty;
        Description = string.Empty;
    }

    public SupportTicketId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public long Number { get; private set; }

    public string DisplayNumber => SupportTicketNumber.Format(Number);

    public SupportTicketTypeId TypeId { get; private set; }

    public string TypeName { get; private set; }

    public int? TermBusinessDays { get; private set; }

    public bool TermIsLegal { get; private set; }

    public bool RequiresFormalResponse { get; private set; }

    public string Subject { get; private set; }

    public string Description { get; private set; }

    public SupportTicketIntake Intake { get; private set; }

    public ManualIntakeMedium? ManualMedium { get; private set; }

    public SupportReplyChannel ReplyChannel { get; private set; }

    public SupportTicketStatus Status { get; private set; }

    public Guid? AssigneeMemberId { get; private set; }

    /// <summary>FK blanda a Customers: Guid y no un id tipado, como en el resto de módulos.</summary>
    public Guid? CustomerId { get; private set; }

    public TicketRequester? Requester { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>La fecha local de radicación en la zona del tenant: desde el día hábil siguiente cuenta el plazo.</summary>
    public DateOnly ReceivedOn { get; private set; }

    /// <summary>Se recalcula sólo al crear, al reanudar y al cambiar el tipo (plan D4). Detenido, conserva el último valor.</summary>
    public DateOnly? DueOn { get; private set; }

    /// <summary>Desde cuándo está detenido el reloj; una cadena de detenciones conserva el inicio de la primera.</summary>
    public DateTimeOffset? PausedSince { get; private set; }

    public int PausedBusinessDays { get; private set; }

    /// <summary>Cuántas veces entró a <see cref="SupportTicketStatus.Resolved"/>: reabrir y resolver otra vez es otro ciclo.</summary>
    public int ResolutionCount { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public int LastEventOrdinal { get; private set; }

    /// <summary>Los eventos de esta instancia: al crear, el <see cref="SupportTicketEventKind.Created"/>; cargado de la base, sólo
    /// los que se agreguen después (el repositorio no los carga).</summary>
    public IReadOnlyList<SupportTicketEvent> Events => events;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>Plan D6: un ticket manual necesita cliente o solicitante con nombre.</summary>
    public static SupportTicket CreateManual(
        SupportTicketId id,
        Guid tenantId,
        long number,
        SupportTicketType type,
        string subject,
        string description,
        ManualIntakeMedium medium,
        SupportReplyChannel replyChannel,
        Guid? customerId,
        TicketRequester? requester,
        Guid? assigneeMemberId,
        Guid actorMemberId,
        DateTimeOffset now,
        SupportClockContext clock)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        EnsureSameTenant(tenantId, type);
        if (customerId is null && requester is not { HasName: true })
        {
            throw RequesterRequired();
        }

        var ticket = new SupportTicket
        {
            Id = id,
            TenantId = tenantId,
            Number = number,
            Subject = Text(subject, SubjectMaxLength, nameof(subject)),
            Description = Text(description, DescriptionMaxLength, nameof(description)),
            Intake = SupportTicketIntake.Manual,
            ManualMedium = medium,
            ReplyChannel = replyChannel,
            Status = SupportTicketStatus.New,
            AssigneeMemberId = assigneeMemberId,
            CustomerId = customerId,
            Requester = requester,
            ReceivedAt = now,
            ReceivedOn = clock.LocalDate(now),
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
        ticket.CopyType(type, clock);
        ticket.AddEvent(SupportTicketEventKind.Created, actorMemberId, now, new { to = SupportTicketStatus.New.ToString() });
        return ticket;
    }

    public void ChangeStatus(SupportTicketStatus target, Guid actorMemberId, DateTimeOffset now, SupportClockContext clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!SupportTicketStatuses.AllowedFrom(Status).Contains(target))
        {
            throw new SupportDomainException(
                SupportErrorCodes.TransitionInvalid, $"A ticket cannot go from '{Status}' to '{target}'.");
        }

        var from = Status;
        var wasStopped = SupportTicketStatuses.StopsTheClock(from);
        var stops = SupportTicketStatuses.StopsTheClock(target);
        if (!wasStopped && stops)
        {
            PausedSince = now;
        }
        else if (wasStopped && !stops && PausedSince is { } pausedSince)
        {
            PausedBusinessDays += SupportBusinessDays.CountStrictlyBetween(
                clock.Calendar, clock.LocalDate(pausedSince), clock.LocalDate(now));
            PausedSince = null;
            DueOn = DueOnFor(TermBusinessDays, clock);
        }

        Status = target;
        if (target is SupportTicketStatus.Resolved)
        {
            ResolutionCount++;
            ResolvedAt = now;
        }
        else if (target is SupportTicketStatus.Closed)
        {
            ClosedAt = now;
        }

        AddEvent(SupportTicketEventKind.StatusChanged, actorMemberId, now, new { from = from.ToString(), to = target.ToString() });
    }

    /// <summary>Pre-flight R9: se permite en cualquier estado, también resuelto o cerrado; no cambia el estado.</summary>
    public void Assign(Guid? assigneeMemberId, Guid actorMemberId, DateTimeOffset now)
    {
        if (assigneeMemberId == AssigneeMemberId)
        {
            return;
        }

        var from = AssigneeMemberId;
        AssigneeMemberId = assigneeMemberId;
        AddEvent(SupportTicketEventKind.AssigneeChanged, actorMemberId, now, new { from, to = assigneeMemberId });
    }

    /// <summary>Recalcula el vencimiento desde la radicación con el término nuevo más las pausas ya contadas (spec §7.2).</summary>
    public void ChangeType(SupportTicketType type, Guid actorMemberId, DateTimeOffset now, SupportClockContext clock)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(clock);
        EnsureSameTenant(TenantId, type);
        if (type.Id == TypeId)
        {
            return;
        }

        var from = new { id = TypeId.Value, name = TypeName };
        CopyType(type, clock);
        AddEvent(SupportTicketEventKind.TypeChanged, actorMemberId, now, new { from, to = new { id = TypeId.Value, name = TypeName } });
    }

    /// <summary>Desvincular deja la foto del solicitante; si no hay foto con nombre, el ticket quedaría sin nadie y se rechaza
    /// (pre-flight R11).</summary>
    public void LinkCustomer(Guid? customerId, Guid actorMemberId, DateTimeOffset now)
    {
        if (customerId == CustomerId)
        {
            return;
        }

        if (customerId is null && Requester is not { HasName: true })
        {
            throw RequesterRequired();
        }

        var from = CustomerId;
        CustomerId = customerId;
        AddEvent(SupportTicketEventKind.CustomerChanged, actorMemberId, now, new { from, to = customerId });
    }

    private void CopyType(SupportTicketType type, SupportClockContext clock)
    {
        TypeId = type.Id;
        TypeName = type.Name;
        TermBusinessDays = type.TermBusinessDays;
        TermIsLegal = type.TermIsLegal;
        RequiresFormalResponse = type.RequiresFormalResponse;
        DueOn = DueOnFor(type.TermBusinessDays, clock);
    }

    private DateOnly? DueOnFor(int? term, SupportClockContext clock) =>
        term is { } days ? SupportBusinessDays.Add(clock.Calendar, ReceivedOn, days + PausedBusinessDays) : null;

    private void AddEvent(SupportTicketEventKind kind, Guid actorMemberId, DateTimeOffset now, object details)
    {
        LastEventOrdinal++;
        events.Add(SupportTicketEvent.Create(
            TenantId, Id, LastEventOrdinal, kind, actorMemberId, now, JsonSerializer.Serialize(details)));
        if (kind is not SupportTicketEventKind.Created)
        {
            UpdatedAt = now;
            Version++;
        }
    }

    private static void EnsureSameTenant(Guid tenantId, SupportTicketType type)
    {
        if (type.TenantId != tenantId)
        {
            throw new ArgumentException("The ticket type belongs to another tenant.", nameof(type));
        }
    }

    private static string Text(string value, int maxLength, string name)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength)
        {
            throw new ArgumentException($"The text must have between 1 and {maxLength} characters.", name);
        }

        return trimmed;
    }

    private static SupportDomainException RequesterRequired() =>
        new(SupportErrorCodes.RequesterRequired, "A ticket needs a linked customer or a requester with a name.");
}
